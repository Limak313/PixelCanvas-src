namespace PixelCanvas;

public sealed class Layer
{
    public string Name { get; set; }
    public bool Visible { get; set; }
    public PixelGrid Grid { get; }
    public History History { get; private set; } = new();

    public void SetHistoryBudget(HistoryBudget budget)
    {
        History.Clear();
        History = new History(budget);
    }

    public Layer(string name, PixelGrid grid, bool visible = true)
    {
        Name = name;
        Grid = grid;
        Visible = visible;
    }
}
