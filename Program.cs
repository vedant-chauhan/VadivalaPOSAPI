using Microsoft.EntityFrameworkCore;
using CloudKitchenPOS.Data;
using CloudKitchenPOS.Models;

var builder = WebApplication.CreateBuilder(args);

// 1. Add Database Context
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. Add CORS Policy
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFlutter",
        policy => policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader());
});

// Add services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// 3. Use the CORS Policy
app.UseCors("AllowFlutter");

// Swagger
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapGet("/menu", async (AppDbContext db) =>
{
    return await db.MenuItems
        .Where(m => m.IsActive == true)
        .ToListAsync();
});

app.MapPost("/order", async (AppDbContext db, Order order) =>
{
    using var transaction = await db.Database.BeginTransactionAsync();

    try
    {
        order.CreatedAt = DateTime.Now;
        order.OrderDateTime = DateTime.Now;

        // 🔥 STEP 1: DETACH items first
        var items = order.OrderItems?.ToList() ?? new List<OrderItem>();
        order.OrderItems = new List<OrderItem>();

        // 🔥 STEP 2: Save order ONLY
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        decimal totalAmount = 0;

        // 🔥 STEP 3: Process items manually
        foreach (var item in items)
        {
            var menuItem = await db.MenuItems.FindAsync(item.MenuItemID);

            if (menuItem == null)
                return Results.BadRequest("Invalid Menu Item");

            item.PriceAtTime = menuItem.Price;
            item.OrderID = order.OrderID;

            totalAmount += (item.PriceAtTime ?? 0) * item.Quantity;

            db.OrderItems.Add(item); // ✅ ADD manually
        }

        order.TotalAmount = totalAmount;

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Results.Ok(order);
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync();
        return Results.BadRequest(ex.InnerException?.Message ?? ex.Message);
    }
});

app.MapPut("/menu/{id}/toggle", async (int id, AppDbContext db) =>
{
    var item = await db.MenuItems.FindAsync(id);

    if (item == null) return Results.NotFound();

    item.IsActive = !(item.IsActive ?? true);
    await db.SaveChangesAsync();

    return Results.Ok(item);
});
app.MapPost("/menu", async (AppDbContext db, MenuItem item) =>
{
    item.CreatedAt = DateTime.Now;
    db.MenuItems.Add(item);
    await db.SaveChangesAsync();
    return Results.Ok(item); 
});

app.MapGet("/menu/all", async (AppDbContext db) =>
{
    return await db.MenuItems.ToListAsync();
});
app.MapGet("/categories", async (AppDbContext db) =>
{
    return await db.Categories
        .Select(x => new
        {
            categoryID = x.CategoryID,
            categoryName = x.CategoryName
        })
        .ToListAsync();
});
app.MapPut("/menu/{id}", async (int id, MenuItem updated, AppDbContext db) =>
{
    var item = await db.MenuItems.FindAsync(id);

    if (item == null) return Results.NotFound();

    item.Name = updated.Name;
    item.Price = updated.Price;
    // item.ImageURL = updated.ImageURL;

    await db.SaveChangesAsync();

    return Results.Ok(item);
});
app.MapGet("/dashboard/summary", async (AppDbContext db) =>
{
    var today = DateTime.Today;

    var todayOrders = await db.Orders
        .CountAsync(o => o.OrderDateTime.Date == today);

    var todayRevenue = await db.Orders
        .Where(o => o.OrderDateTime.Date == today)
        .SumAsync(o => (decimal?)o.TotalAmount) ?? 0;

    var avgOrderValue = todayOrders > 0
        ? todayRevenue / todayOrders
        : 0;

    var activeItems = await db.MenuItems
        .CountAsync(x => x.IsActive == true);

    return Results.Ok(new
    {
        todayOrders,
        todayRevenue,
        avgOrderValue,
        activeItems
    });
});

app.MapGet("/dashboard/weekly", async (AppDbContext db) =>
{
    var startDate = DateTime.Today.AddDays(-6);

    var orders = await db.Orders
        .Where(o => o.OrderDateTime >= startDate)
        .GroupBy(o => o.OrderDateTime.Date)
        .Select(g => new
        {
            Date = g.Key,
            Orders = g.Count(),
            Revenue = g.Sum(x => x.TotalAmount)
        })
        .ToListAsync();

    var result = Enumerable.Range(0, 7)
        .Select(i =>
        {
            var date = startDate.AddDays(i);
            var existing = orders.FirstOrDefault(x => x.Date == date);

            return new
            {
                day = date.ToString("ddd"),
                orders = existing?.Orders ?? 0,
                revenue = existing?.Revenue ?? 0
            };
        });

    return Results.Ok(result);
});
app.MapGet("/report", async (
    AppDbContext db,
    DateTime from,
    DateTime to) =>
{
    to = to.Date.AddDays(1).AddTicks(-1);

    // Orders in range
    var orders = await db.Orders
        .Where(o => o.OrderDateTime >= from &&
                    o.OrderDateTime <= to)
        .ToListAsync();

    var orderIds = orders
        .Select(o => o.OrderID)
        .ToList();

    // Order Items + Menu join
    var items = await (
        from oi in db.OrderItems
        join mi in db.MenuItems
        on oi.MenuItemID equals mi.MenuItemID
        where orderIds.Contains(oi.OrderID)
        group new { oi, mi } by mi.Name into g
        select new
        {
            name = g.Key,
            quantity = g.Sum(x => x.oi.Quantity),
            amount = g.Sum(x => x.oi.TotalPrice ?? 0)
        }
    ).ToListAsync();

    // Revenue trend
    var trend = orders
        .GroupBy(o => o.OrderDateTime.Date)
        .Select(g => new
        {
            day = g.Key.ToString("dd MMM"),
            amount = g.Sum(x => x.TotalAmount)
        })
        .OrderBy(x => x.day)
        .ToList();

    var totalQuantity = items.Sum(x => x.quantity);
    var grandTotal = items.Sum(x => x.amount);

    return Results.Ok(new
    {
        totalQuantity,
        grandTotal,
        trend,
        items
    });
});
app.Run();
