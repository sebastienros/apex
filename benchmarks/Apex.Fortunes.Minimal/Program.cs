using System.Text.Encodings.Web;
using System.Text.Unicode;
using Apex.Fortunes.Minimal;
using Apex.Fortunes.Minimal.Templates;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();

await using var database = await FortuneDatabase.CreateAsync(builder.Configuration);
await database.ValidateAsync(CancellationToken.None);
builder.Services.AddSingleton(CreateHtmlEncoder());

await using var app = builder.Build();

switch (database)
{
    case Utf8FortuneDatabase utf8Database:
        app.MapGet(
            "/fortunes",
            async (HtmlEncoder htmlEncoder, CancellationToken cancellationToken) =>
            {
                var fortunes = await utf8Database.LoadAsync(cancellationToken);
                var template = Utf8Fortunes.Create(fortunes);
                template.HtmlEncoder = htmlEncoder;
                return template;
            });
        break;
    case StringFortuneDatabase stringDatabase:
        app.MapGet(
            "/fortunes",
            async (HtmlEncoder htmlEncoder, CancellationToken cancellationToken) =>
            {
                var fortunes = await stringDatabase.LoadAsync(cancellationToken);
                var template = Fortunes.Create(fortunes);
                template.HtmlEncoder = htmlEncoder;
                return template;
            });
        break;
    default:
        throw new InvalidOperationException(
            $"Unsupported fortune database type '{database.GetType().Name}'.");
}

app.Lifetime.ApplicationStarted.Register(static () => Console.WriteLine("Application started."));

await app.RunAsync();

static HtmlEncoder CreateHtmlEncoder()
{
    var settings = new TextEncoderSettings(
        UnicodeRanges.BasicLatin,
        UnicodeRanges.Katakana,
        UnicodeRanges.Hiragana);
    settings.AllowCharacter('\u2014');
    return HtmlEncoder.Create(settings);
}
