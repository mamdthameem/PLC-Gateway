using PlcApi.Models;

namespace PlcApi.Services;

public interface ILifetimeService
{
    Task<List<LifetimeParameterDto>> GetAllAsync();
}
