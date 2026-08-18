using System.Text.Encodings.Web;
using System.Text.Unicode;
using Apex.Fortunes.Minimal;
using Apex.Fortunes.Minimal.Templates;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();

await using var database = await FortuneDatabase.CreateAsync(builder.Configuration);
builder.Services.AddSingleton<FortuneDatabase>(database);
builder.Services.AddSingleton(CreateHtmlEncoder());

await using var app = builder.Build();

app.MapGet(
    "/fortunes",
    static async (
        FortuneDatabase database,
        HtmlEncoder htmlEncoder,
        CancellationToken cancellationToken) =>
    {
        var fortunes = await database.LoadAsync(cancellationToken);
        var template = Fortunes.Create(fortunes);
        template.HtmlEncoder = htmlEncoder;
        return template;
    });

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
