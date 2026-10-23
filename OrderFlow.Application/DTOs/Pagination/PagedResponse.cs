using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Application.DTOs.Pagination;

public record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);