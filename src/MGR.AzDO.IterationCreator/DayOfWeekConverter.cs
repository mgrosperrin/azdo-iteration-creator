using MGR.CommandLineParser.Extensibility.Converters;

namespace MGR.AzDO.IterationCreator;

public class DayOfWeekConverter : IConverter
{
    public Type TargetType => typeof(DayOfWeek);

    public object Convert(string value, Type concreteTargetType)
    {
        return Enum.Parse<DayOfWeek>(value);
    }
}