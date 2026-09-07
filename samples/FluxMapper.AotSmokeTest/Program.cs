// Real Native AOT publish smoke test for FluxMapper's source-generator execution tier. See this
// project's csproj for why it deliberately avoids FluxMapper.Core entirely.
using FluxMapper.Abstractions;

var order = new Order { Id = 7, Total = 42.5m };

// OrderDto.MapFrom is emitted at compile time by FluxMapper.SourceGenerator from the [MapFrom] attribute
// below -- zero reflection, zero Expression.Compile(). If this project fails to publish under
// publishAot=true, or the published native executable produces the wrong answer, that is a genuine
// AOT-safety regression.
var dto = OrderDto.MapFrom(order);

var ok = dto.Id == order.Id && dto.Total == order.Total;
Console.WriteLine($"AOT smoke test: Id={dto.Id} (expected {order.Id}), Total={dto.Total} (expected {order.Total})");
Console.WriteLine(ok ? "AOT SMOKE TEST PASSED" : "AOT SMOKE TEST FAILED");
return ok ? 0 : 1;

public class Order
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

[MapFrom(typeof(Order))]
public partial class OrderDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}
