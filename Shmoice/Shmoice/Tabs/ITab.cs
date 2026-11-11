namespace Shmoice.Tabs;

/// <summary>A tab that needs the main window (its data, history, logging, running commands).</summary>
public interface ITab
{
    void Attach(MainWindow host);
}
