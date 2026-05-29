using System;

namespace CryptoAITerminal.Core.Models;

/// <summary>Current funding rate for one perpetual instrument.</summary>
public readonly record struct FundingRateSnapshot(
    string   Symbol,
    decimal  FundingRate,      // raw, e.g. 0.0001 = 0.01 %
    decimal  MarkPrice,
    DateTime NextFundingTime   // UTC
);

/// <summary>One historical funding rate data point.</summary>
public readonly record struct FundingHistoryPoint(
    DateTime Time,             // UTC
    decimal  Rate              // raw decimal
);
