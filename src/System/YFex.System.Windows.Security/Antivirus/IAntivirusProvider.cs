namespace YFex.System.Windows.Security.Antivirus;

public interface IAntivirusProvider
{
    DefenderStatus GetStatus();
}
