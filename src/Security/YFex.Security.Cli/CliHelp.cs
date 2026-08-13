namespace YFex.Security.Cli;

public static class CliHelp
{
    public static void Print()
    {
        Console.WriteLine("""
            yfex-security — YFex network & process security intelligence

            USAGE:
              yfex-security <command> [options]

            SERVICE:
              start [--pcap]                     Start the background capture service (admin required)
              status                             Show events/anomalies captured today

            QUERY:
              query events  [--process N] [--type T] [--after DT] [--before DT] [--limit N]
              query connections [--process N] [--remote IP] [--limit N]
              query dns     [--domain PATTERN] [--process N] [--limit N]

            REPORTS:
              report daily   [--date YYYY-MM-DD] [--output FILE]
              report process <name> [--output FILE]
              report api     <service> [--output FILE]
              report security [--from DT] [--to DT] [--output FILE]

            BASELINES:
              baseline rebuild [--type domain_per_process|ja3_per_process|all]

            ANOMALIES:
              anomalies [--severity critical|high|medium|low] [--limit N]

            ALLOWLIST:
              allowlist domain  add <domain>
              allowlist domain  list
              allowlist process add <name>
              allowlist process list

            MAINTENANCE:
              purge [--older-than 90d]

            Run 'yfex-security help' to show this message.
            """);
    }
}
