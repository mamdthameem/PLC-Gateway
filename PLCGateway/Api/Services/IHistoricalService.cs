using PlcApi.Models;

namespace PlcApi.Services;

public interface IHistoricalService
{
    Task<List<HistoricalPointDto>> GetPointsAsync(string parameterName, DateTime start, DateTime end);
}
