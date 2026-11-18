using System.Net.Http.Json;
using System.Text.Json;

namespace Scada.Server.Tests;

internal static class BatchConfirmationTestHelpers
{
    public static async Task<string> SequenceChallengeAsync(this HttpClient client, int sequenceId)
    {
        var grant = await client.PostAsJsonAsync($"/api/control/sequences/{sequenceId}/check-start", new { });
        grant.EnsureSuccessStatusCode();
        return (await grant.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challenge").GetString()!;
    }

    public static async Task<HttpResponseMessage> StartSequenceAsync(this HttpClient client, int sequenceId, bool confirmed = true)
    {
        string? challenge = null;
        if (confirmed)
        {
            var grant = await client.PostAsJsonAsync($"/api/control/sequences/{sequenceId}/check-start", new { });
            if (grant.IsSuccessStatusCode)
                challenge = (await grant.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challenge").GetString();
        }
        return await client.PostAsJsonAsync($"/api/control/sequences/{sequenceId}/start", new { confirmed, challenge });
    }

    public static async Task<HttpResponseMessage> DownloadRecipeAsync(this HttpClient client, int recipeId, int version, bool confirmed = true)
    {
        string? challenge = null;
        var check = await client.PostAsJsonAsync($"/api/recipes/{recipeId}/check", new { version });
        if (check.IsSuccessStatusCode)
            challenge = (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmationChallenge").GetString();
        return await client.PostAsJsonAsync($"/api/recipes/{recipeId}/download", new { version, confirmed, challenge });
    }
}