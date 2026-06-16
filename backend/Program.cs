using System.Text;
using LgsImpact.Api.Middleware;
using LgsImpact.Api.Services;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Cosmos;
using Microsoft.IdentityModel.Tokens;
using OfficeOpenXml;

var builder = WebApplication.CreateBuilder(args);

ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

// ─── Application Insights ────────────────────────────────────────────────────
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddSingleton<ITelemetryInitializer, PiiTelemetryInitializer>();

// ─── CORS ────────────────────────────────────────────────────────────────────
var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:3000"];

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

// ─── JWT Authentication ───────────────────────────────────────────────────────
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret not configured");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = builder.Configuration["Jwt:Issuer"],
            ValidAudience            = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew                = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode  = 401;
                ctx.Response.ContentType = "application/json";
                return ctx.Response.WriteAsync("{\"message\":\"Unauthorized\"}");
            }
        };
    });

builder.Services.AddAuthorization();

// ─── Cosmos DB ───────────────────────────────────────────────────────────────
builder.Services.AddSingleton<ICosmosDbService, CosmosDbService>();

// ─── Application Services ────────────────────────────────────────────────────
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IPiiRedactionService, PiiRedactionService>();
builder.Services.AddScoped<ISchoolAverageService, SchoolAverageService>();

// ─── LLM Provider ─────────────────────────────────────────────────────────────
// LlmProvider:Name = "groq"      → GroqProvider      (free hosted, no infra — default for Azure)
// LlmProvider:Name = "meta-llama"→ MetaLlamaProvider (self-hosted Ollama serving Llama)
// LlmProvider:Name = "ollama"    → OllamaProvider    (local dev, any model)
// LlmProvider:Name = "groq"        → GroqProvider        (free hosted, Meta Llama — default for Azure)
// LlmProvider:Name = "openrouter"  → OpenRouterProvider  (free hosted, many models incl. Llama/Mistral/Qwen)
// LlmProvider:Name = "meta-llama"  → MetaLlamaProvider   (self-hosted Ollama serving Llama)
// LlmProvider:Name = "ollama"      → OllamaProvider      (local dev, any model)
var llmProviderName = builder.Configuration["LlmProvider:Name"] ?? "groq";
if (llmProviderName.Equals("groq", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<ILlmProvider, GroqProvider>();
    builder.Services.AddScoped<ILlmService, GroqProvider>();
}
else if (llmProviderName.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<ILlmProvider, OpenRouterProvider>();
    builder.Services.AddScoped<ILlmService, OpenRouterProvider>();
}
else if (llmProviderName.Equals("meta-llama", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<ILlmProvider, MetaLlamaProvider>();
    builder.Services.AddScoped<ILlmService, MetaLlamaProvider>();
}
else
{
    builder.Services.AddScoped<ILlmProvider, OllamaProvider>();
    builder.Services.AddScoped<ILlmService, OllamaProvider>();
}

builder.Services.AddScoped<ITierCalculationService, TierCalculationService>();
builder.Services.AddScoped<IBlobStorageService, BlobStorageService>();
builder.Services.AddHttpClient("ollama").SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient("llm").SetHandlerLifetime(TimeSpan.FromMinutes(5));

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

builder.Services.AddControllers();
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "LGS Impact API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new()
    {
        Name         = "Authorization",
        Type         = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        In           = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description  = "Enter your JWT token"
    });
    c.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
            []
        }
    });
});

var app = builder.Build();

// ─── Ensure Cosmos containers exist + seed admins ────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var cosmos = scope.ServiceProvider.GetRequiredService<ICosmosDbService>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var endpoint = config["Cosmos:Endpoint"]!;
    var key      = config["Cosmos:Key"]!;
    var dbId     = config["Cosmos:DatabaseId"] ?? "lgs-impact";

    var client = new CosmosClient(endpoint, key);
    await CosmosDbService.EnsureDatabaseAndContainersAsync(client, dbId);
    await cosmos.SeedAdminsIfEmptyAsync();
}

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "LGS Impact API v1"));

app.UseCors();
app.UseHttpsRedirection();
app.UseHsts();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<PiiAuditMiddleware>();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new { service = "LGS Impact API", version = "2.0.0", status = "running", db = "cosmos" }));

app.Run();
