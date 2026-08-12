using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

internal static class SqlTransactionExtensions
{
    public static async Task RollbackBestEffortAsync(
        this SqlTransaction transaction,
        Exception originalException)
    {
        try
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception rollbackException)
        {
            // The operation's original exception is the causal evidence. A broken
            // connection must not let a secondary rollback failure replace it.
            originalException.Data["MesIngest.RollbackFailure"] = rollbackException.ToString();
        }
    }
}
