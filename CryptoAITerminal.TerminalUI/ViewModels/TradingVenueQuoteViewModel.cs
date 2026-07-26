using System;
using System.Globalization;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// One exchange tab in the trading desk's venue switcher. Carries the live
/// mid price of the currently selected symbol on this venue plus its delta
/// versus the base venue (Binance), so the user can compare and switch the
/// active price/order-book source for the same token.
/// </summary>
public class TradingVenueQuoteViewModel : ReactiveObject
{
    private decimal _price;
    private decimal _basePrice;
    private bool _isSelected;
    private bool _isBase;
    private bool _isStale = true;

    public TradingVenueQuoteViewModel(string exchange)
    {
        Exchange = exchange;
    }

    /// <summary>Gateway key: "Binance", "Bybit", "OKX", "KuCoin".</summary>
    public string Exchange { get; }

    public decimal Price
    {
        get => _price;
        private set
        {
            this.RaiseAndSetIfChanged(ref _price, value);
            RaiseDerived();
        }
    }

    public decimal BasePrice
    {
        get => _basePrice;
        private set
        {
            this.RaiseAndSetIfChanged(ref _basePrice, value);
            RaiseDerived();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(TabBackground));
            this.RaisePropertyChanged(nameof(TabBorderBrush));
            this.RaisePropertyChanged(nameof(NameBrush));
        }
    }

    public bool IsBase
    {
        get => _isBase;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBase, value);
            RaiseDerived();
        }
    }

    /// <summary>True until the first quote arrives (or when the venue has no data for the symbol).</summary>
    public bool IsStale
    {
        get => _isStale;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isStale, value);
            RaiseDerived();
        }
    }

    public string PriceLabel => Price > 0m ? Price.ToString("N2", CultureInfo.InvariantCulture) : "--";

    /// <summary>Price difference versus the base venue in quote currency.</summary>
    public decimal Delta => IsBase || BasePrice <= 0m || Price <= 0m ? 0m : Price - BasePrice;

    public string DeltaLabel
    {
        get
        {
            if (IsBase) return "base";
            if (Price <= 0m || BasePrice <= 0m) return "--";
            var d = Delta;
            return d.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        }
    }

    public string DeltaBrush => IsBase
        ? SemanticColor.Muted
        : Delta >= 0m ? SemanticColor.Positive : SemanticColor.Negative;

    public string TabBackground => IsSelected ? "#0E2A2E" : "#07111A";
    public string TabBorderBrush => IsSelected ? SemanticColor.Accent : "#152535";
    public string NameBrush => IsSelected ? "#F4F7FB" : "#9BAFC5";

    /// <summary>Apply a fresh quote from the venue poller.</summary>
    public void Update(decimal price, decimal basePrice, bool hasData)
    {
        _price = price;
        _basePrice = basePrice;
        IsStale = !hasData;
        this.RaisePropertyChanged(nameof(Price));
        this.RaisePropertyChanged(nameof(BasePrice));
        RaiseDerived();
    }

    private void RaiseDerived()
    {
        this.RaisePropertyChanged(nameof(PriceLabel));
        this.RaisePropertyChanged(nameof(Delta));
        this.RaisePropertyChanged(nameof(DeltaLabel));
        this.RaisePropertyChanged(nameof(DeltaBrush));
    }
}

/// <summary>One row in the public trade tape under the chart.</summary>
public class TapeTradeViewModel
{
    public TapeTradeViewModel(bool isBuy, decimal price, decimal quantity, DateTime timeLocal)
    {
        IsBuy = isBuy;
        Price = price;
        Quantity = quantity;
        TimeLocal = timeLocal;
    }

    public bool IsBuy { get; }
    public decimal Price { get; }
    public decimal Quantity { get; }
    public DateTime TimeLocal { get; }

    public string SideLabel => IsBuy ? "BUY" : "SELL";
    public string SideBrush => IsBuy ? SemanticColor.Keys.Positive : SemanticColor.Keys.Negative;
    public string PriceLabel => Price.ToString("N2", CultureInfo.InvariantCulture);
    public string QuantityLabel => Quantity.ToString("0.###", CultureInfo.InvariantCulture);
    public string TimeLabel => TimeLocal.ToString("HH:mm:ss");
}
