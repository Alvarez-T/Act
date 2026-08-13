namespace YFex.Windows.Security.Antivirus;

public interface IAntivirusProvider
{
    DefenderStatus GetStatus();
}
