namespace MesIngest.Host;

internal static class ProductionHostStartupPolicy
{
    public static void Validate(
        MesIngestHostOptions options,
        bool isDevelopment,
        bool probeOracle,
        bool projectionEnabled)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (isDevelopment)
        {
            return;
        }

        if (!options.IsOracleSnapshotSource())
        {
            throw new InvalidOperationException(
                "MesIngest:SnapshotSource must be Oracle outside the Development environment.");
        }

        // A live Oracle probe is an explicit short-lived production mode. It does
        // not serve the API and intentionally needs no SQL projection database.
        if (probeOracle)
        {
            return;
        }

        if (!projectionEnabled)
        {
            throw new InvalidOperationException(
                "MesIngest:NewSqlServerConnectionString is required outside the Development environment.");
        }

        var explicitProducerMode = options.ContinuousPollEnabled
                                   || options.RunOneShotOnStartup
                                   || ReleaseSmokeRoundReplay.IsConfigured(options);
        if (!explicitProducerMode)
        {
            throw new InvalidOperationException(
                "At least one explicit producer mode is required outside Development: enable "
                + "MesIngest:ContinuousPollEnabled or MesIngest:RunOneShotOnStartup. "
                + "The recorded-round release smoke remains an explicit smoke-only mode.");
        }
    }
}
