namespace YFex.System.Data;

public record AdapterInfo(
    string Name,
    string HashedMac,
    string[] MaskedIpAddresses,
    long SpeedBps,
    bool IsUp
);

public record NetworkSnapshot(
    AdapterInfo[] Adapters,
    bool HasNetworkAccess
);
