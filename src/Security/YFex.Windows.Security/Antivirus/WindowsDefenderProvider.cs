using System.Management;

namespace YFex.Windows.Security.Antivirus;

public sealed class WindowsDefenderProvider : IAntivirusProvider
{
    public DefenderStatus GetStatus()
    {
        var products = new List<AntivirusProductInfo>();
        bool anyRealTime = false;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\SecurityCenter2",
                "SELECT * FROM AntiVirusProduct");

            using var results = searcher.Get();
            foreach (var obj in results)
            {
                string displayName = obj["displayName"]?.ToString() ?? "Unknown";
                string instanceGuid = obj["instanceGuid"]?.ToString() ?? "";
                string pathToExe = obj["pathToSignedProductExe"]?.ToString() ?? "";
                uint productState = Convert.ToUInt32(obj["productState"]);

                bool isEnabled = ((productState >> 12) & 0xF) != 0;
                bool isUpToDate = ((productState >> 4) & 0xF) == 0;

                if (isEnabled) anyRealTime = true;

                products.Add(new AntivirusProductInfo(
                    displayName, instanceGuid, pathToExe, isEnabled, isUpToDate));

                obj.Dispose();
            }
        }
        catch { }

        return new DefenderStatus(products, anyRealTime);
    }
}
