using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// --- HTTP Client: Inventory Service ---
builder.Services.AddHttpClient<InventoryServiceClient>(client =>
{
    var baseUrl = Environment.GetEnvironmentVariable("INVENTORY_SVC_URL") ?? "http://ppops-inventory-svc:8080";
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// --- HTTP Client: Auth Service ---
builder.Services.AddHttpClient<AuthServiceClient>(client =>
{
    var baseUrl = Environment.GetEnvironmentVariable("AUTH_SVC_URL") ?? "http://ppops-auth-svc:8080";
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();

app.MapControllers();
app.Run();

// --- HTTP Client Classes ---
public class InventoryServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InventoryServiceClient> _logger;

    public InventoryServiceClient(HttpClient httpClient, ILogger<InventoryServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<JsonDocument?> GetStockLevelAsync(string partId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/api/inventory/stock/{partId}");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get stock level for {PartId} from inventory-svc", partId);
            return null;
        }
    }
}

public class AuthServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AuthServiceClient> _logger;

    public AuthServiceClient(HttpClient httpClient, ILogger<AuthServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> ValidateTokenAsync(string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/validate");
            request.Headers.Add("Authorization", $"Bearer {token}");
            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate token with auth-svc");
            return false;
        }
    }
}

// --- Models ---
public record ProcurementRequest(string PartId, int Quantity, string Justification, string RequestedBy, string Priority);
public record ProcurementOrder(string OrderId, string PartId, int Quantity, string Status, string RequestedBy, double EstimatedCost, DateTime CreatedAt, DateTime? ApprovedAt);

// --- Controller ---
[ApiController]
[Route("api/procurement")]
public class ProcurementController : ControllerBase
{
    private static readonly List<ProcurementOrder> _orders = new();
    private readonly InventoryServiceClient _inventoryClient;
    private readonly AuthServiceClient _authClient;
    private readonly ILogger<ProcurementController> _logger;

    public ProcurementController(InventoryServiceClient inventoryClient, AuthServiceClient authClient, ILogger<ProcurementController> logger)
    {
        _inventoryClient = inventoryClient;
        _authClient = authClient;
        _logger = logger;
    }

    [HttpPost("request")]
    public async Task<IActionResult> CreateRequest([FromBody] ProcurementRequest request)
    {
        var token = HttpContext.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
        if (!string.IsNullOrEmpty(token))
        {
            var isValid = await _authClient.ValidateTokenAsync(token);
            if (!isValid)
                return Unauthorized(new { error = "Invalid authentication token" });
        }

        var stockInfo = await _inventoryClient.GetStockLevelAsync(request.PartId);

        var order = new ProcurementOrder(
            OrderId: $"PO-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}",
            PartId: request.PartId,
            Quantity: request.Quantity,
            Status: request.Priority == "urgent" ? "auto-approved" : "pending-approval",
            RequestedBy: request.RequestedBy,
            EstimatedCost: request.Quantity * (50.0 + Random.Shared.NextDouble() * 500),
            CreatedAt: DateTime.UtcNow,
            ApprovedAt: request.Priority == "urgent" ? DateTime.UtcNow : null
        );

        _orders.Add(order);
        _logger.LogInformation("Procurement order {OrderId} created for {Quantity}x {PartId}", order.OrderId, request.Quantity, request.PartId);
        return Ok(order);
    }

    [HttpGet("orders")]
    public IActionResult GetOrders([FromQuery] string? status = null, [FromQuery] int limit = 50)
    {
        var query = _orders.AsEnumerable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(o => o.Status == status);

        var results = query.OrderByDescending(o => o.CreatedAt).Take(limit).ToList();
        return Ok(new { count = results.Count, orders = results });
    }
}