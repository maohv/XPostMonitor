using System.Net.Http.Headers;
using System.Net.Http.Json;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;

namespace XPostMonitor.Services;

public sealed class XApiClient
{
    private readonly HttpClient httpClient;
    private readonly string bearerToken;

    public XApiClient(HttpClient httpClient, BotOptions options)
    {
        this.httpClient = httpClient;
        bearerToken = options.XBearerToken;
    }

    public async Task<XUser?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        string url = "2/users/by/username/" + Uri.EscapeDataString(username) + "?user.fields=protected";

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        XUserResponse? result = await response.Content.ReadFromJsonAsync<XUserResponse>(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return result?.Data;
        }

        string errorMessage = response.ReasonPhrase ?? "Unknown error";
        if (result?.Errors != null && result.Errors.Count > 0)
        {
            errorMessage = result.Errors[0].Detail ?? errorMessage;
        }

        throw new HttpRequestException("X API: " + errorMessage, null, response.StatusCode);
    }
}
