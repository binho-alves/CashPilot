namespace CashPilot.Domain.Payments;

/// <summary>
/// Credit card cycle arithmetic. Assumption (confirm per card): a purchase made ON the closing day still belongs to
/// the bill that closes that day. The due date is the first due day strictly after the closing date.
/// Days beyond the month length are clamped (day 31 in February becomes the 28th/29th).
/// </summary>
public static class CardBilling
{
    public static DateOnly DayOfMonth(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    /// <summary>Closing date of the bill that contains a purchase made on <paramref name="date"/>.</summary>
    public static DateOnly ClosingFor(DateOnly date, int closingDay)
    {
        var closing = DayOfMonth(date.Year, date.Month, closingDay);
        if (date <= closing) return closing;
        var next = date.AddMonths(1);
        return DayOfMonth(next.Year, next.Month, closingDay);
    }

    public static DateOnly DueFor(DateOnly closing, int dueDay)
    {
        var due = DayOfMonth(closing.Year, closing.Month, dueDay);
        if (due > closing) return due;
        var next = closing.AddMonths(1);
        return DayOfMonth(next.Year, next.Month, dueDay);
    }
}
