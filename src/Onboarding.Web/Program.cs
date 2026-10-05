// Placeholder until phase 2 (Blazor Server, Entra ID auth, dev-auth mode).
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Onboarding.Web – phase 2 pending");

await app.RunAsync();
