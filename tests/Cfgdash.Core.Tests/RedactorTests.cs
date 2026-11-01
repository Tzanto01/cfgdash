using System.Text.Json.Nodes;
using Cfgdash.Core.Redaction;

namespace Cfgdash.Core.Tests;

public sealed class RedactorTests
{
    private const string R = "[redacted]";
    private readonly Redactor redactor = new();

    [Theory]
    [InlineData("apiToken")]
    [InlineData("client_secret")]
    [InlineData("x-api-key")]
    [InlineData("APIKey")]
    [InlineData("OAuthToken")]
    [InlineData("DB_PASSWORD")]
    [InlineData("Authorization")]
    [InlineData("headers")]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS")]
    [InlineData("sessionId")]
    [InlineData("myAccessKeyId")]
    [InlineData("sshPrivateKeyPath")]
    [InlineData("ANTHROPIC_APIKEY")]
    public void IsSecretName_flags_names_with_a_secret_word_part_or_substring(string name) =>
        Assert.True(redactor.IsSecretName(name));

    [Theory]
    [InlineData("model")]
    [InlineData("keyboardLayout")]
    [InlineData("monkeyBusiness")]
    [InlineData("passthrough")]
    [InlineData("author")]
    public void IsSecretName_keeps_names_that_only_contain_a_secret_word_inside_another_word(string name) =>
        Assert.False(redactor.IsSecretName(name));

    [Fact]
    public void RedactJson_replaces_any_value_type_under_a_secret_key_and_keeps_the_rest()
    {
        var input = new JsonObject
        {
            ["apiKey"] = new JsonObject { ["nested"] = "value" },
            ["password"] = 123,
            ["tokens"] = new JsonArray("a", "b"),
            ["auth"] = true,
            ["name"] = "server",
            ["timeout"] = 5,
            ["enabled"] = false,
        };

        var output = redactor.RedactJson(input)!.AsObject();

        foreach (var key in new[] { "apiKey", "password", "tokens", "auth" })
        {
            Assert.Equal(R, output[key]!.GetValue<string>());
        }

        Assert.Equal("server", output["name"]!.GetValue<string>());
        Assert.Equal(5, output["timeout"]!.GetValue<int>());
        Assert.False(output["enabled"]!.GetValue<bool>());
        Assert.Equal(123, input["password"]!.GetValue<int>());
    }

    [Fact]
    public void RedactJson_redact_all_values_keeps_keys_and_redacts_every_leaf()
    {
        var input = new JsonObject { ["X-Custom"] = "v", ["n"] = 1, ["list"] = new JsonArray("a", 2) };

        var output = redactor.RedactJson(input, redactAllValues: true)!.AsObject();

        Assert.Equal(["X-Custom", "n", "list"], output.Select(p => p.Key));
        Assert.Equal(R, output["X-Custom"]!.GetValue<string>());
        Assert.Equal(R, output["n"]!.GetValue<string>());
        Assert.All(output["list"]!.AsArray(), item => Assert.Equal(R, item!.GetValue<string>()));
    }

    [Fact]
    public void RedactJson_applies_value_patterns_to_strings_under_non_secret_keys()
    {
        var url = "postgres://user" + ":" + "hunter2pw" + "@" + "db.example/app";
        var output = redactor.RedactJson(new JsonObject { ["args"] = new JsonArray("--port", "5432", url) })!;

        Assert.Equal("postgres://user:" + R + "@db.example/app", output["args"]![2]!.GetValue<string>());
        Assert.Equal("5432", output["args"]![1]!.GetValue<string>());
    }

    public static TheoryData<string> HighConfidenceSecrets() =>
    [
        "AK" + "IA" + new string('A', 16),
        "xo" + "xb-" + new string('1', 12),
        "gh" + "p_" + new string('a', 36),
        "github" + "_pat_" + new string('a', 60),
        "sk-" + "ant-" + new string('a', 24),
        "sk-" + "proj-" + new string('a', 24),
        "s" + "k-" + new string('a', 32),
        "sk" + "_live_" + new string('a', 24),
        "rk" + "_live_" + new string('a', 24),
        "AI" + "za" + new string('a', 35),
    ];

    [Theory]
    [MemberData(nameof(HighConfidenceSecrets))]
    public void RedactText_removes_every_high_confidence_secret_family(string secret)
    {
        var output = redactor.RedactText("value " + secret + " end");

        Assert.Equal("value " + R + " end", output);
    }

    [Fact]
    public void RedactText_keeps_the_account_key_prefix()
    {
        var text = "DefaultEndpointsProtocol=https;" + "Account" + "Key=" + new string('a', 50) + "==;EndpointSuffix=core.example";

        Assert.Equal("DefaultEndpointsProtocol=https;AccountKey=" + R + ";EndpointSuffix=core.example", redactor.RedactText(text));
    }

    [Fact]
    public void RedactText_url_credentials_keep_scheme_user_and_host()
    {
        var text = "remote https://user" + ":" + "s3cr3tpw" + "@" + "git.example/repo.git";

        Assert.Equal("remote https://user:" + R + "@git.example/repo.git", redactor.RedactText(text));
    }

    [Fact]
    public void RedactText_password_literal_is_redacted_but_placeholders_stay()
    {
        var real = "Server=db.example;" + "Password" + "=" + "Xq9vLm2pZ" + ";Database=app";
        var placeholder = "Server=db.example;" + "Password" + "=" + "changeme123" + ";Database=app";

        Assert.Equal("Server=db.example;Password=" + R + ";Database=app", redactor.RedactText(real));
        Assert.Equal(placeholder, redactor.RedactText(placeholder));
    }

    [Fact]
    public void RedactText_bearer_value_is_redacted()
    {
        var text = "Authorization: Bearer " + "abc" + ".def-ghi";

        Assert.Equal("Authorization: Bearer " + R, redactor.RedactText(text));
    }

    [Theory]
    [InlineData("--token", " ")]
    [InlineData("--api-key", "=")]
    [InlineData("--password", "=")]
    [InlineData("--secret", " ")]
    [InlineData("--key", " ")]
    public void RedactText_cli_flags_keep_the_flag_and_redact_the_value(string flag, string separator)
    {
        var output = redactor.RedactText("tool " + flag + separator + "abc" + "123xyz --verbose");

        Assert.Equal("tool " + flag + separator + R + " --verbose", output);
    }

    [Theory]
    [InlineData("notify = [\"ntfy\", \"--token\", \"", "\"]")]
    [InlineData("args: ['srv', '--api-key', '", "']")]
    [InlineData("[\"srv\",\"--password\",\"", "\"]")]
    public void RedactText_a_secret_flag_and_its_value_in_separate_list_items_redacts_the_value(string before, string after)
    {
        Assert.Equal(before + R + after, redactor.RedactText(before + "q7" + "Zx9vLm" + after));
    }

    [Fact]
    public void RedactJson_redacts_the_array_item_after_a_bare_secret_flag()
    {
        var input = new JsonArray("srv", "--api-key", "q7" + "Zx9vLm", "--port", "8080", "-p", "q7" + "Zx9vLm");

        var output = redactor.RedactJson(input)!.AsArray().Select(n => n!.GetValue<string>());

        Assert.Equal(["srv", "--api-key", R, "--port", "8080", "-p", "q7" + "Zx9vLm"], output);
    }

    [Theory]
    [InlineData("tool --access-token ", " --verbose")]
    [InlineData("tool --auth-token=", " --verbose")]
    [InlineData("tool --client-secret ", " --verbose")]
    [InlineData("tool --personal-access-token ", " --verbose")]
    [InlineData("tool -pwd ", " --verbose")]
    [InlineData("run(--credential=", " )")]
    public void RedactText_any_flag_with_a_secret_word_redacts_its_value(string before, string after)
    {
        var output = redactor.RedactText(before + "q7" + "Zx9vLm" + after);

        Assert.Equal(before + R + after, output);
    }

    [Theory]
    [InlineData("DB_PASS=")]
    [InlineData("SIGNING_KEY=")]
    [InlineData("client-key-data: ")]
    [InlineData("SESSION_ID=")]
    [InlineData("Authorization: ")]
    [InlineData("const signingKey = ")]
    [InlineData("encryptionKey: ")]
    [InlineData("dbPass=")]
    [InlineData("basicAuth: ")]
    public void RedactText_assignment_to_a_secret_name_word_redacts_the_value(string prefix)
    {
        Assert.Equal(prefix + R + " end", redactor.RedactText(prefix + "q7" + "Zx9vLm end"));
    }

    [Fact]
    public void RedactText_basic_auth_and_netrc_passwords_are_redacted()
    {
        var basic = "Authorization: Basic " + "dXNlcjpw" + "YXNzd29yZA==";
        var netrc = "machine git.example login user password " + "q7" + "Zx9vLm";

        Assert.Equal("Authorization: Basic " + R, redactor.RedactText(basic));
        Assert.Equal("machine git.example login user password " + R, redactor.RedactText(netrc));
    }

    [Fact]
    public void RedactText_pgp_private_key_block_is_removed()
    {
        var text = "a\n-----BEGIN PGP " + "PRIVATE KEY" + " BLOCK-----\nlQOYBF" + new string('Q', 40) + "\n-----END PGP PRIVATE KEY BLOCK-----\nb";

        Assert.Equal("a\n" + R + "\nb", redactor.RedactText(text));
    }

    [Theory]
    [InlineData("ssh-keygen -t ed25519 -f out")]
    [InlineData("The monkey: a keyboard layout for the author, see the passthrough docs.")]
    public void RedactText_keeps_hyphenated_commands_and_prose_without_secret_words(string text) =>
        Assert.Equal(text, redactor.RedactText(text));

    public static TheoryData<string> HostileInputs() =>
    [
        "x://" + new string(':', 200_000),
        string.Concat(Enumerable.Repeat("token-", 500 * 1024 / 6)),
    ];

    [Theory]
    [MemberData(nameof(HostileInputs))]
    public void RedactText_runs_in_linear_time_on_hostile_input(string text)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        redactor.RedactText(text);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Fact]
    public void RedactText_pem_private_key_block_body_is_removed()
    {
        var body = "MIIEow" + new string('Q', 40);
        var text = "before\n-----BEGIN " + "RSA PRIVATE" + " KEY-----\n" + body + "\n-----END RSA PRIVATE KEY-----\nafter";

        var output = redactor.RedactText(text);

        Assert.Equal("before\n" + R + "\nafter", output);
    }

    [Fact]
    public void RedactText_unterminated_pem_block_is_removed_to_the_end()
    {
        var text = "x\n-----BEGIN " + "PRIVATE" + " KEY-----\n" + "MIIEow" + new string('Q', 40);

        Assert.Equal("x\n" + R, redactor.RedactText(text));
    }

    [Fact]
    public void RedactText_is_idempotent()
    {
        var text = string.Join("\n",
            "postgres://user" + ":" + "hunter2pw" + "@" + "db.example",
            "Bearer " + "abc" + "def",
            "--token " + "abc" + "def",
            "Password" + "=" + "Xq9vLm2pZ",
            "api_token: \"" + "abc" + "def\"",
            "gh" + "p_" + new string('a', 36));

        var once = redactor.RedactText(text);
        var twice = redactor.RedactText(once);

        Assert.Equal(once, twice);
        Assert.DoesNotContain(R + R, twice);
        Assert.DoesNotContain("abcdef", twice);
        Assert.DoesNotContain("hunter2pw", twice);
    }
}
