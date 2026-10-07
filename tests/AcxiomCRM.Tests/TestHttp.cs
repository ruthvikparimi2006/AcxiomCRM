using System.Text.RegularExpressions;

namespace AcxiomCRM.Tests;

// Form posts through the real anti-forgery check: fetch a page, copy its token, post.
public static partial class TestHttp
{
    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url,
        Dictionary<string, string> fields, string? formPage = null)
    {
        var html = await client.GetStringAsync(formPage ?? url);
        fields["__RequestVerificationToken"] = AntiforgeryField().Match(html).Groups[1].Value;
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client, string login, string password) =>
        PostFormAsync(client, "/Account/Login", new() { ["Login"] = login, ["Password"] = password });

    // A client already signed in as the given user.
    public static async Task<HttpClient> SignedInAsync(DbFixture fx, string login, string password)
    {
        var client = fx.NewClient();
        var response = await LoginAsync(client, login, password);
        if (response.StatusCode != System.Net.HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Login failed for {login}.");
        return client;
    }
}
