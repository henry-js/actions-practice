public class NestedSettings
{
    public const string Key = nameof(NestedSettings);
    public string Item3 { get; set; } = default!;
    public Deeper Deeper { get; set; } = default!;

}

public class Deeper
{
    public string Item4 { get; set; } = default!;
}