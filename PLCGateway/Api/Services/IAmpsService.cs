using PlcApi.Models;

namespace PlcApi.Services;

public interface IAmpsService
{
    Task<List<AmpReadingDto>> GetImpellerAmpsAsync();
}
