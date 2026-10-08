namespace CashPilot.Web;

/// <summary>Light, non-blocking messages ("Salvo") shown by <c>ToastHost</c>. One instance per browser session.</summary>
public sealed class ToastService
{
    public event Action<string, bool>? Shown;

    public void Success(string message) => Shown?.Invoke(message, false);

    public void Error(string message) => Shown?.Invoke(message, true);
}
