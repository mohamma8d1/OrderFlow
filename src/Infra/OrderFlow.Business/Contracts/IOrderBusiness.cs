using OrderFlow.Common.DTOs.Order;
using OrderFlow.Common.DTOs.Pagination;

namespace OrderFlow.Business.Contracts;

public interface IOrderBusiness
{
    Task<OrderCreatedResponse> CreateAsync(Guid userId, CreateOrderDto dto, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid userId, Guid orderId, CancellationToken cancellationToken = default);
    Task<PagedResponse<OrderSummaryResponse>> GetMyOrdersAsync(Guid userId, int page, int size, CancellationToken cancellationToken = default);
    Task<OrderDetailsResponse> GetByIdAsync(Guid userId, Guid orderId, CancellationToken cancellationToken = default);
}
