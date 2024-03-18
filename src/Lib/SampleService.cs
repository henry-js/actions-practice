namespace actions_practice.Lib.Services;

public class SampleService : ISampleService
{
    private readonly string _message;

    public SampleService(string message)
    {
        _message = message;
    }
    public bool DoWork()
    {
        Console.WriteLine($"Simulating doing work in service. Message from constructor: {_message}");

        return true;
    }
}

public interface ISampleService
{
    bool DoWork();
}