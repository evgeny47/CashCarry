using CashCarry.Core.Broker;
using CashCarry.Core.Market;

namespace CashCarry.Broker.Paper;

public enum InstrumentKind { Stock, Futures }

/// <summary>
/// Инструмент симулятора. Для акции LotSize — штук в лоте; для фьючерса LotSize = 1,
/// PointValue — руб. за пункт, InitialMargin — ГО на контракт (можно менять через SetInitialMargin).
/// </summary>
public sealed record PaperInstrument(
    string Symbol, InstrumentKind Kind, int LotSize = 1, decimal PointValue = 1m, decimal InitialMargin = 0m);

/// <summary>
/// Симулятор брокера. Исполняет лимитные заявки по лучшей цене стакана в пределах доступного объёма,
/// ведёт деньги, ГО и вариационную маржу (до клиринга — нереализованную), отклоняет заявки без покрытия
/// и шорт акций. Котировки подаются извне через SetQuote. Потокобезопасен.
/// </summary>
public sealed class PaperBroker : IBrokerGateway
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PaperInstrument> _instruments;
    private readonly Dictionary<string, decimal> _margins;
    private readonly CommissionSchedule _commissions;
    private readonly Dictionary<string, Book> _books = new();
    private readonly Dictionary<string, int> _positions = new();
    // Для фьючерсов: стоимость позиции по ценам сделок/последнего клиринга. ВМ = позиция × цена × PV − это значение.
    private readonly Dictionary<string, decimal> _futuresOpenValue = new();
    private readonly Dictionary<string, PaperOrder> _orders = new();
    private readonly Dictionary<string, string> _pendingRejects = new();
    private decimal _cash;

    public PaperBroker(IEnumerable<PaperInstrument> instruments, CommissionSchedule commissions, decimal initialCash)
    {
        _instruments = instruments.ToDictionary(i => i.Symbol);
        _margins = _instruments.Values.ToDictionary(i => i.Symbol, i => i.InitialMargin);
        _commissions = commissions;
        _cash = initialCash;
    }

    /// <summary>Обновить котировку; bidLots/askLots — доступный объём на лучших ценах. Активные заявки перепроверяются.</summary>
    public void SetQuote(string symbol, Quote quote, int bidLots = int.MaxValue, int askLots = int.MaxValue)
    {
        lock (_sync)
        {
            Instrument(symbol);
            _books[symbol] = new Book(quote, bidLots, askLots);
            foreach (var order in _orders.Values.Where(o => o.Symbol == symbol && o.Status == OrderStatus.Working).ToList())
                TryMatch(order);
        }
    }

    public void SetInitialMargin(string symbol, decimal margin)
    {
        lock (_sync)
        {
            if (Instrument(symbol).Kind != InstrumentKind.Futures)
                throw new ArgumentException($"{symbol} — не фьючерс.", nameof(symbol));
            _margins[symbol] = margin;
        }
    }

    /// <summary>Следующая заявка по инструменту будет отклонена с указанной причиной.</summary>
    public void RejectNext(string symbol, string reason)
    {
        lock (_sync) _pendingRejects[symbol] = reason;
    }

    /// <summary>
    /// Восстановить позицию без сделки и комиссии (после перезапуска: состояние симулятора живёт только в памяти).
    /// Для акций списывает стоимость из денег, для фьючерса фиксирует цену как базу вариационной маржи.
    /// </summary>
    public void SeedPosition(string symbol, int quantity, decimal price)
    {
        lock (_sync)
        {
            var instrument = Instrument(symbol);
            if (instrument.Kind == InstrumentKind.Stock)
                _cash -= quantity * instrument.LotSize * price;
            else
                _futuresOpenValue[symbol] = _futuresOpenValue.GetValueOrDefault(symbol) + quantity * price * instrument.PointValue;
            _positions[symbol] = _positions.GetValueOrDefault(symbol) + quantity;
        }
    }

    /// <summary>Зачислить дивиденд (после налога) на текущий лонг по акции.</summary>
    public void PayDividend(string symbol, decimal netPerShare)
    {
        lock (_sync)
        {
            var instrument = Instrument(symbol);
            _cash += Math.Max(0, _positions.GetValueOrDefault(symbol)) * instrument.LotSize * netPerShare;
        }
    }

    /// <summary>Клиринг: вариационная маржа по середине спреда переходит в деньги.</summary>
    public void RunClearing()
    {
        lock (_sync)
        {
            foreach (var (symbol, qty) in _positions
                         .Where(p => Instrument(p.Key).Kind == InstrumentKind.Futures && _books.ContainsKey(p.Key)).ToList())
            {
                decimal settle = Mid(symbol);
                decimal value = qty * settle * Instrument(symbol).PointValue;
                _cash += value - _futuresOpenValue.GetValueOrDefault(symbol);
                _futuresOpenValue[symbol] = value;
            }
        }
    }

    public Task<OrderState> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_orders.ContainsKey(request.ClientOrderId))
                throw new InvalidOperationException($"Заявка {request.ClientOrderId} уже существует.");

            var order = new PaperOrder(request);
            _orders[request.ClientOrderId] = order;

            string? reject = Validate(request);
            if (reject is not null)
            {
                order.Status = OrderStatus.Rejected;
                order.RejectReason = reject;
            }
            else
            {
                TryMatch(order);
            }

            return Task.FromResult(order.ToState());
        }
    }

    public Task<OrderState> CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var order = Order(clientOrderId);
            if (order.Status == OrderStatus.Working)
                order.Status = OrderStatus.Cancelled;
            return Task.FromResult(order.ToState());
        }
    }

    public Task<OrderState> GetOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult(Order(clientOrderId).ToState());
    }

    public Task<Quote> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        lock (_sync)
        {
            return _books.TryGetValue(symbol, out var book)
                ? Task.FromResult(book.Quote)
                : throw new InvalidOperationException($"Нет котировки {symbol}.");
        }
    }

    public Task<AccountState> GetAccountAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            decimal vm = VariationMargin();
            decimal im = InitialMarginBlocked();
            return Task.FromResult(new AccountState(
                _cash, _cash + vm - im, im, vm,
                _positions.Where(p => p.Value != 0).ToDictionary(p => p.Key, p => p.Value)));
        }
    }

    private string? Validate(OrderRequest request)
    {
        if (!_instruments.TryGetValue(request.Symbol, out var instrument))
            return $"unknown instrument {request.Symbol}";
        if (request.Quantity <= 0 || request.LimitPrice <= 0)
            return "invalid quantity or price";
        if (_pendingRejects.Remove(request.Symbol, out string? injected))
            return injected;

        int position = _positions.GetValueOrDefault(request.Symbol);
        decimal free = _cash + VariationMargin() - InitialMarginBlocked();

        if (instrument.Kind == InstrumentKind.Stock)
        {
            if (request.Side == OrderSide.Sell && request.Quantity > position)
                return "short selling not allowed";
            if (request.Side == OrderSide.Buy)
            {
                int shares = request.Quantity * instrument.LotSize;
                decimal cost = request.LimitPrice * shares + _commissions.ForStock(request.LimitPrice, shares);
                if (cost > free)
                    return "insufficient funds";
            }
        }
        else
        {
            int signed = request.Side == OrderSide.Buy ? request.Quantity : -request.Quantity;
            int added = Math.Abs(position + signed) - Math.Abs(position);
            if (added > 0 && added * _margins[request.Symbol] + _commissions.ForFutures(request.Quantity) > free)
                return "insufficient margin";
        }

        return null;
    }

    private void TryMatch(PaperOrder order)
    {
        if (!_books.TryGetValue(order.Symbol, out var book))
            return;

        bool buy = order.Side == OrderSide.Buy;
        decimal price = buy ? book.Quote.Ask : book.Quote.Bid;
        bool marketable = buy ? order.LimitPrice >= price : order.LimitPrice <= price;
        int available = buy ? book.AskLots : book.BidLots;
        int qty = Math.Min(order.Quantity - order.FilledQuantity, available);
        if (!marketable || qty <= 0)
            return;

        _books[order.Symbol] = buy ? book with { AskLots = available - qty } : book with { BidLots = available - qty };
        Fill(order, qty, price);
    }

    private void Fill(PaperOrder order, int qty, decimal price)
    {
        var instrument = Instrument(order.Symbol);
        int signed = order.Side == OrderSide.Buy ? qty : -qty;
        decimal commission;

        if (instrument.Kind == InstrumentKind.Stock)
        {
            int shares = qty * instrument.LotSize;
            commission = _commissions.ForStock(price, shares);
            _cash -= signed * instrument.LotSize * price;
        }
        else
        {
            commission = _commissions.ForFutures(qty);
            _futuresOpenValue[order.Symbol] = _futuresOpenValue.GetValueOrDefault(order.Symbol)
                + signed * price * instrument.PointValue;
        }

        _cash -= commission;
        _positions[order.Symbol] = _positions.GetValueOrDefault(order.Symbol) + signed;

        order.AvgFillPrice = (order.AvgFillPrice * order.FilledQuantity + price * qty) / (order.FilledQuantity + qty);
        order.FilledQuantity += qty;
        order.Commission += commission;
        if (order.FilledQuantity == order.Quantity)
            order.Status = OrderStatus.Filled;
    }

    private decimal VariationMargin() =>
        _positions.Where(p => Instrument(p.Key).Kind == InstrumentKind.Futures && _books.ContainsKey(p.Key))
            .Sum(p => p.Value * Mid(p.Key) * Instrument(p.Key).PointValue - _futuresOpenValue.GetValueOrDefault(p.Key));

    private decimal InitialMarginBlocked() =>
        _positions.Where(p => Instrument(p.Key).Kind == InstrumentKind.Futures)
            .Sum(p => Math.Abs(p.Value) * _margins[p.Key]);

    private decimal Mid(string symbol)
    {
        var q = _books[symbol].Quote;
        return (q.Bid + q.Ask) / 2;
    }

    private PaperInstrument Instrument(string symbol) =>
        _instruments.TryGetValue(symbol, out var i) ? i : throw new ArgumentException($"Неизвестный инструмент {symbol}.");

    private PaperOrder Order(string id) =>
        _orders.TryGetValue(id, out var o) ? o : throw new KeyNotFoundException($"Заявка {id} не найдена.");

    private sealed record Book(Quote Quote, int BidLots, int AskLots);

    private sealed class PaperOrder(OrderRequest request)
    {
        public string Symbol => request.Symbol;
        public OrderSide Side => request.Side;
        public int Quantity => request.Quantity;
        public decimal LimitPrice => request.LimitPrice;
        public int FilledQuantity { get; set; }
        public decimal AvgFillPrice { get; set; }
        public decimal Commission { get; set; }
        public OrderStatus Status { get; set; } = OrderStatus.Working;
        public string? RejectReason { get; set; }

        public OrderState ToState() => new(
            request.ClientOrderId, Symbol, Side, Quantity, FilledQuantity, AvgFillPrice, Commission, Status, RejectReason);
    }
}
