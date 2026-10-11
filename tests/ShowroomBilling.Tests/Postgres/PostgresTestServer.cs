using System.Diagnostics;
using System.Text.RegularExpressions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// The PostgreSQL 18 server the database tests run against, found or started
/// in this order:
/// <list type="number">
/// <item><c>SHOWROOM_BILLING_POSTGRES_TEST_CONNECTION</c>, when set: a server
/// someone else started (the role must be allowed to create databases).</item>
/// <item>A throwaway container started through the <c>docker</c> command, when
/// the current Docker context is a remote engine (<c>ssh://</c> or
/// <c>tcp://</c>). Testcontainers only talks to a local engine.</item>
/// <item>A Testcontainers container on the local Docker engine. This is what
/// CI uses.</item>
/// </list>
/// </summary>
internal sealed class PostgresTestServer : IAsyncDisposable
{
    internal const string ExternalConnectionStringVariable = "SHOWROOM_BILLING_POSTGRES_TEST_CONNECTION";

    private const string Image = "postgres:18";

    // Parallel test classes each hold a few connections, and every API the
    // contract tests boot keeps its own pool; the default of 100 is too few
    // on a machine with many cores.
    private const string MaxConnections = "max_connections=300";

    // A container the tests could not stop (the run was killed) stops itself.
    private const int ContainerLifetimeSeconds = 2 * 60 * 60;

    private readonly string baseConnectionString;
    private readonly Func<ValueTask> stop;

    private PostgresTestServer(string baseConnectionString, Func<ValueTask> stop)
    {
        this.baseConnectionString = baseConnectionString;
        this.stop = stop;
    }

    public string ConnectionStringFor(string database, bool pooling = true)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = database,
            Pooling = pooling,
            // Puts the offending row or constraint in a failed test's message.
            IncludeErrorDetail = true
        };
        return builder.ConnectionString;
    }

    public ValueTask DisposeAsync() => stop();

    public static async Task<PostgresTestServer> StartAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            return new PostgresTestServer(external, static () => ValueTask.CompletedTask);
        }

        var remoteEndpoint = await FindRemoteDockerEndpointAsync();
        try
        {
            return remoteEndpoint is null
                ? await StartWithTestcontainersAsync()
                : await StartWithDockerCommandAsync(remoteEndpoint);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The database tests need PostgreSQL 18 and could not start one. Start Docker (a local engine, or a " +
                $"remote Docker context the docker command can reach), or point {ExternalConnectionStringVariable} at a " +
                "PostgreSQL 18 server whose role may create databases. Cause: " + ex.Message,
                ex);
        }
    }

    private static async Task<PostgresTestServer> StartWithTestcontainersAsync()
    {
        // The module already turns durability off (fsync and friends); this
        // argument is appended to its own.
        var container = new PostgreSqlBuilder(Image)
            .WithCommand("-c", MaxConnections)
            .Build();
        await container.StartAsync();
        return new PostgresTestServer(container.GetConnectionString(), container.DisposeAsync);
    }

    private static async Task<PostgresTestServer> StartWithDockerCommandAsync(Uri endpoint)
    {
        const string password = "postgres";
        var host = await ResolveHostAddressAsync(endpoint);
        var name = $"tw-postgres-tests-{Guid.NewGuid():N}"[..30];

        // `timeout` is the container's main process and ends the server after
        // ContainerLifetimeSeconds. It passes the stop signal on to the server.
        await RunAsync(
            "docker",
            [
                "run", "-d", "--rm",
                "--name", name,
                "-e", $"POSTGRES_PASSWORD={password}",
                "-p", "0:5432",
                "--entrypoint", "timeout",
                Image,
                "-s", "INT", "-k", "30", ContainerLifetimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "docker-entrypoint.sh", "postgres",
                "-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off",
                "-c", MaxConnections
            ],
            // Long enough for a first pull of the image.
            TimeSpan.FromMinutes(5));

        // Stopped when the tests end; the process-exit hook covers a run whose
        // fixture was never disposed, without waiting for the command.
        var stopped = 0;
        async ValueTask StopContainerAsync()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
            {
                return;
            }

            try
            {
                await RunAsync("docker", ["stop", name], TimeSpan.FromSeconds(60));
            }
            catch
            {
                // The container stops itself after ContainerLifetimeSeconds.
            }
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (Interlocked.Exchange(ref stopped, 1) == 0)
            {
                TryStartDetached("docker", ["stop", name]);
            }
        };

        try
        {
            var portOutput = await RunAsync("docker", ["port", name, "5432/tcp"], TimeSpan.FromSeconds(30));
            var port = ParsePublishedPort(portOutput);
            var connectionString =
                $"Host={host};Port={port};Database=postgres;Username=postgres;Password={password}";
            await WaitUntilAcceptingConnectionsAsync(connectionString, name);

            return new PostgresTestServer(connectionString, StopContainerAsync);
        }
        catch
        {
            await StopContainerAsync();
            throw;
        }
    }

    /// <summary>
    /// The current Docker context's endpoint when it is another machine, or
    /// null for a local engine, an explicit <c>DOCKER_HOST</c> (Testcontainers
    /// reads that itself) or a PC without the docker command.
    /// </summary>
    private static async Task<Uri?> FindRemoteDockerEndpointAsync()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return null;
        }

        string output;
        try
        {
            output = await RunAsync(
                "docker",
                ["context", "inspect", "--format", "{{.Endpoints.docker.Host}}"],
                TimeSpan.FromSeconds(15));
        }
        catch
        {
            return null;
        }

        return Uri.TryCreate(output.Trim(), UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "ssh" or "tcp"
            ? endpoint
            : null;
    }

    /// <summary>
    /// The address this PC reaches the Docker host on. An <c>ssh://</c>
    /// context may name an alias from the SSH configuration, so the SSH client
    /// is asked what it resolves to.
    /// </summary>
    private static async Task<string> ResolveHostAddressAsync(Uri endpoint)
    {
        if (endpoint.Scheme != "ssh")
        {
            return endpoint.Host;
        }

        var target = string.IsNullOrEmpty(endpoint.UserInfo) ? endpoint.Host : $"{endpoint.UserInfo}@{endpoint.Host}";
        List<string> arguments = ["-G"];
        if (endpoint.Port > 0)
        {
            arguments.AddRange(["-p", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        arguments.Add(target);

        var config = await RunAsync("ssh", arguments, TimeSpan.FromSeconds(15));
        var match = Regex.Match(config, @"^hostname\s+(\S+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : endpoint.Host;
    }

    internal static int ParsePublishedPort(string dockerPortOutput)
    {
        var lines = dockerPortOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = lines.FirstOrDefault(static x => x.StartsWith("0.0.0.0:", StringComparison.Ordinal)) ?? lines.FirstOrDefault();
        var match = Regex.Match(line ?? string.Empty, @":(\d+)$");
        return match.Success
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Could not read the published PostgreSQL port from: {dockerPortOutput}");
    }

    private static async Task WaitUntilAcceptingConnectionsAsync(string connectionString, string containerName)
    {
        // Over TCP: while the image runs its first-time set-up the server
        // listens on its local socket only.
        var probe = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Timeout = 3 }.ConnectionString;
        var deadline = Stopwatch.StartNew();
        Exception? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(90))
        {
            try
            {
                await using var connection = new NpgsqlConnection(probe);
                await connection.OpenAsync();
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
            {
                last = ex;
                await Task.Delay(300);
            }
        }

        throw new InvalidOperationException(
            $"PostgreSQL container '{containerName}' did not accept connections within 90 seconds: {last?.Message}", last);
    }

    private static async Task<string> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        // Nothing can answer a prompt (an SSH passphrase, say): fail, don't wait.
        process.StandardInput.Close();

        using var cancellation = new CancellationTokenSource(timeout);
        var output = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var error = process.StandardError.ReadToEndAsync(cancellation.Token);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"{fileName} {string.Join(' ', startInfo.ArgumentList)} did not finish within {timeout.TotalSeconds:0} seconds.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', startInfo.ArgumentList)} failed with exit code {process.ExitCode}: {(await error).Trim()}");
        }

        return await output;
    }

    private static void TryStartDetached(string fileName, IEnumerable<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var process = Process.Start(startInfo);
        }
        catch
        {
            // The container stops itself after ContainerLifetimeSeconds.
        }
    }
}
