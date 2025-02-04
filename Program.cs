using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Stripe;
using Stripe.Checkout;
using Stripe.Forwarding;
using System.Collections.Generic;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAllOrigins", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});
var configuration = builder.Configuration;
var stripeSettingsSection = configuration.GetSection("Stripe");
builder.Services.Configure<StripeSettings>(stripeSettingsSection);

var stripeSettings = stripeSettingsSection.Get<StripeSettings>();
StripeConfiguration.ApiKey = stripeSettings.SecretKey;
// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors("AllowAllOrigins");
app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapPost("/create-stripe-customer", async (string name, string email, string systemId) =>
{
    try
    {
        var options = new CustomerCreateOptions
        {
            Email = email,
            Name = name,
            //edit metadata to include total price for order
            Metadata = new Dictionary<string , string>
         {
            { "ID", systemId },
            { "TotalPrice", "1000" }
         }
        };
        var service = new CustomerService();
        Customer c = await service.CreateAsync(options);
        return true;
    }
    catch (Exception ex)
    {

       return false;
    }
});
//test customers
async Task<Customer?> GetCustomerByEmail(string email)
{
    var service = new CustomerService();
    var stripeCustomers = await service.ListAsync(new CustomerListOptions()
    {
        Email = email
    });
    return stripeCustomers.FirstOrDefault();
}
app.MapPost("/payment-intent", async (string email) =>
{
    var service = new PaymentIntentService();
    var customer = await GetCustomerByEmail(email);

    if (customer == null)
        return Results.Json(new { success = false, message = "Customer not found" });

    if (!customer.Metadata.TryGetValue("TotalPrice", out var totalPriceStr) ||
        !int.TryParse(totalPriceStr, out var totalPrice))
    {
        return Results.Json(new { success = false, message = "Invalid or missing TotalPrice in metadata" });
    }

    var options = new PaymentIntentCreateOptions
    {
        Amount = totalPrice,
        Currency = "usd",
        PaymentMethodTypes = new List<string> { "card" },
    };

    var paymentIntent = await service.CreateAsync(options);

    return Results.Json(new
    {
        success = true,
        Email = email,
        PaymentIntent = options
    });
});
//check if payment sucess or not

app.MapPost("/webhook", async (HttpRequest request) =>
{
    var json = await new StreamReader(request.Body).ReadToEndAsync();
    var yolo = false;
    //for local testing if production set webhook url in stripe dashboard
    const string endpointSecret = "whsec_2e8b5b94bfb28a921416d72edac2d0924a92b92269f431ea0c4ff24ff2896b30";
    try
    {
        var stripeEvent = EventUtility.ParseEvent(json);
        var signatureHeader = request.Headers["Stripe-Signature"];

        stripeEvent = EventUtility.ConstructEvent(json,
                signatureHeader, endpointSecret);

        if (stripeEvent.Type == EventTypes.PaymentIntentSucceeded && yolo == true)
        {
          //code here in case success
        }
        else
        if (stripeEvent.Type == EventTypes.PaymentIntentSucceeded)
        {
            //in case success but product not available
            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            var options = new RefundCreateOptions
            {
                PaymentIntent = paymentIntent.Id,
                //amount has to be same as paid but remove 1 dollar.
                Amount = 950,
            };
            var service = new RefundService();
            service.Create(options);
            return Results.Ok(new { success = true, message = "Payment successful and refunded" });

        }
        else if (stripeEvent.Type == EventTypes.PaymentIntentCanceled ||
                 stripeEvent.Type == EventTypes.PaymentIntentPaymentFailed)
        {
            Console.WriteLine($"⚠️ Payment failed or canceled: {stripeEvent.Type}");
            return Results.BadRequest(new { error = "Payment failed or was canceled" }); 
        }

        Console.WriteLine($"⚠️ Unhandled event type: {stripeEvent.Type}");
        return Results.Ok(new { success = false, message = "Unhandled event type" }); 
    }
    catch (StripeException e)
    {
        Console.WriteLine($"❌ Stripe error: {e.Message}");
        return Results.BadRequest(new { error = "Stripe error", details = e.Message }); // ✅ Detailed error response
    }
});


app.Run();


public class StripeSettings
{
    public string SecretKey { get; set; }
    public string PublishableKey { get; set; }
}