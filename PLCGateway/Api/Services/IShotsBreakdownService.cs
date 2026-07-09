using PlcApi.Models;

namespace PlcApi.Services;

public interface IShotsBreakdownService
{
    Task<List<ShotsBreakdownDto>> GetAllAsync();
}
