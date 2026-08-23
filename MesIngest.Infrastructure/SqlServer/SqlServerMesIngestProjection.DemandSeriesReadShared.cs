using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    private static void BindDemandSeriesFilterParameters(
        SqlCommand command,
        DemandSeriesBrowseFilter normalized)
    {
        AddNVarChar(command, "@lifecyclesJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.Lifecycles,
            static message => new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                message)));
        AddNVarChar(command, "@presencesJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.CurrentPresences,
            static message => new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                message)));
        AddNVarChar(command, "@workTypesJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.WorkTypes,
            static message => new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                message)));
        AddNVarChar(command, "@areasJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.MesAreas,
            static message => new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                message)));
        AddNullableNVarChar(command, "@sublotContains", 256, normalized.SublotContains);
        AddNullableNVarChar(command, "@sublot", 256, normalized.Sublot);
        AddNullableNVarChar(command, "@seriesId", 64, normalized.SeriesId);
        AddNullableNVarChar(command, "@demandId", 64, normalized.DemandId);
    }
}
