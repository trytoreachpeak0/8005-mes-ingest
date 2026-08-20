namespace MesIngest.Tests;

/// <summary>
/// Runs a WPF assertion body on a dedicated STA thread.
/// </summary>
internal static class StaTestRunner
{
    public static void Run(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                caught = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA test did not finish");
        Assert.Null(caught);
    }
}
