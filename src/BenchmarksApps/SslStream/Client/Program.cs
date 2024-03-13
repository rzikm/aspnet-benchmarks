﻿using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using System.CommandLine;
using Microsoft.Crank.EventSources;
using SslStreamCommon;
using SslStreamClient;

// avoid false sharing among counters
internal class Metrics
{
    public double BytesReadPerSecond;
    public double BytesWrittenPerSecond;
}

internal class Program
{
    private static bool s_isRunning;
    private static bool s_isWarmup;

    private static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("SslStream benchmark client");
        OptionsBinder.AddOptions(rootCommand);
        rootCommand.SetHandler<ClientOptions>(Run, new OptionsBinder());
        return await rootCommand.InvokeAsync(args).ConfigureAwait(false);
    }

    static async Task<int> Run(ClientOptions options)
    {
        SetupMeasurements();

        try
        {
            switch (options.Scenario)
            {
                case Scenario.ReadWrite:
                    await RunReadWriteScenario(options).ConfigureAwait(false);
                    break;
                case Scenario.Handshake:
                    await RunHandshakeScenario(options).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown scenario: {options.Scenario}");
            }
        }
        catch (Exception e)
        {
            Log($"Exception occured: {e}");
            return 1;
        }

        return 0;
    }

    static async Task RunHandshakeScenario(ClientOptions options)
    {
        RegisterPercentiledMetric("sslstream/handshake", "Handshake duration (ms)", "Handshakes duration in milliseconds");

        static async Task<List<double>> DoRun(ClientOptions options)
        {
            var values = new List<double>();
            while (s_isRunning)
            {
                using var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await sock.ConnectAsync(options.Hostname, options.Port).ConfigureAwait(false);

                long timestamp = Stopwatch.GetTimestamp();
                await using var stream = await EstablishSslStreamAsync(sock, options).ConfigureAwait(false);
                if (!s_isWarmup)
                {
                    values.Add(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
                }
            }

            return values;
        }

        s_isRunning = true;
        var tasks = new List<Task<List<double>>>();
        for (int i = 0; i < options.Concurrency; i++)
        {
            tasks.Add(Task.Run(() => DoRun(options)));
        }
        await Task.Delay(options.Warmup).ConfigureAwait(false);
        Log("Completing warmup...");

        Stopwatch sw = Stopwatch.StartNew();
        await Task.Delay(options.Duration).ConfigureAwait(false);

        Log("Completing scenario...");
        s_isRunning = false;
        var values = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(x => x).ToList();
        sw.Stop();

        LogPercentiledMetric("sslstream/handshake", values);
    }

    static async Task RunReadWriteScenario(ClientOptions options)
    {
        BenchmarksEventSource.Register("sslstream/write/mean", Operations.Avg, Operations.Avg, "Mean bytes written per second.", "Bytes per second - mean", "n2");

        BenchmarksEventSource.Register("sslstream/read/mean", Operations.Avg, Operations.Avg, "Mean bytes read per second.", "Bytes per second - mean", "n2");

        static async Task<Metrics> RunCore(ClientOptions options)
        {
            using var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await sock.ConnectAsync(options.Hostname, options.Port).ConfigureAwait(false);
            using var stream = await EstablishSslStreamAsync(sock, options).ConfigureAwait(false);

            // spawn the reading and writing tasks on the thread pool, note that if we were to use
            //     var writeTask = WritingTask(stream, options.SendBufferSize);
            // then it could end up writing a lot of data until it finally suspended control back to this function.
            var writeTask = Task.Run(() => WritingTask(stream, options.SendBufferSize));
            var readTask = Task.Run(() => ReadingTask(stream, options.ReceiveBufferSize));

            await Task.WhenAll(writeTask, readTask).ConfigureAwait(false);

            return new Metrics
            {
                BytesReadPerSecond = await readTask,
                BytesWrittenPerSecond = await writeTask
            };
        }

        static async Task<double> WritingTask(SslStream stream, int bufferSize, CancellationToken cancellationToken = default)
        {
            if (bufferSize == 0)
            {
                return 0;
            }

            var sendBuffer = new byte[bufferSize];
            Stopwatch sw = Stopwatch.StartNew();
            bool isWarmup = true;

            long bytesWritten = 0;

            while (s_isRunning)
            {
                if (isWarmup && !s_isWarmup)
                {
                    isWarmup = false;
                    sw.Restart();
                    bytesWritten = 0;
                }

                await stream.WriteAsync(sendBuffer, cancellationToken).ConfigureAwait(false);
                bytesWritten += bufferSize;
            }

            sw.Stop();
            return bytesWritten / sw.Elapsed.TotalSeconds;
        }

        static async Task<double> ReadingTask(SslStream stream, int bufferSize, CancellationToken cancellationToken = default)
        {
            if (bufferSize == 0)
            {
                return 0;
            }

            var recvBuffer = new byte[bufferSize];
            Stopwatch sw = Stopwatch.StartNew();
            bool isWarmup = true;

            long bytesRead = 0;

            while (s_isRunning)
            {
                if (isWarmup && !s_isWarmup)
                {
                    isWarmup = false;
                    sw.Restart();
                    bytesRead = 0;
                }

                bytesRead += await stream.ReadAsync(recvBuffer, cancellationToken).ConfigureAwait(false);
            }

            sw.Stop();
            return bytesRead / sw.Elapsed.TotalSeconds;
        }

        s_isRunning = true;

        var tasks = new List<Task<Metrics>>();

        for (int i = 0; i < options.Concurrency; i++)
        {
            tasks.Add(RunCore(options));
        }

        await Task.Delay(options.Warmup).ConfigureAwait(false);
        Log("Completing warmup...");

        s_isWarmup = false;
        await Task.Delay(options.Duration).ConfigureAwait(false);
        Log("Completing scenario...");
        s_isRunning = false;
        var metrics = await Task.WhenAll(tasks).ConfigureAwait(false);
        LogMetric("sslstream/read/mean", metrics.Sum(x => x.BytesReadPerSecond));
        LogMetric("sslstream/write/mean", metrics.Sum(x => x.BytesWrittenPerSecond));
    }

    static SslClientAuthenticationOptions CreateSslClientAuthenticationOptions(ClientOptions options)
    {
        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = options.TlsHostName ?? options.Hostname,
            RemoteCertificateValidationCallback = delegate { return true; },
            ApplicationProtocols = new List<SslApplicationProtocol> {
                options.Scenario switch {
                    Scenario.ReadWrite => ApplicationProtocolConstants.ReadWrite,
                    Scenario.Handshake => ApplicationProtocolConstants.Handshake,
                    _ => throw new Exception("Unknown scenario")
                }
            },
#if NET8_0_OR_GREATER
            AllowTlsResume = options.AllowTlsResume,
#endif
            EnabledSslProtocols = options.EnabledSslProtocols,
            CertificateRevocationCheckMode = options.CertificateRevocationCheckMode,
        };

        if (options.ClientCertificate != null)
        {
            switch (options.CertificateSelection)
            {
                case CertificateSelectionType.Collection:
                    sslOptions.ClientCertificates = new X509CertificateCollection { options.ClientCertificate };
                    break;
                case CertificateSelectionType.Callback:
                    sslOptions.LocalCertificateSelectionCallback = delegate { return options.ClientCertificate; };
                    break;
#if NET8_0_OR_GREATER
                case CertificateSelectionType.CertContext:
                    sslOptions.ClientCertificateContext = SslStreamCertificateContext.Create(options.ClientCertificate, new X509Certificate2Collection());
                    break;
#endif
                default:
                    throw new InvalidOperationException($"Certificate selection type {options.CertificateSelection} is not supported in this .NET version.");
            }
        }

        return sslOptions;
    }

    static async Task<SslStream> EstablishSslStreamAsync(Socket socket, ClientOptions options)
    {
        var networkStream = new NetworkStream(socket, ownsSocket: true);
        var stream = new SslStream(networkStream, leaveInnerStreamOpen: false);
        await stream.AuthenticateAsClientAsync(CreateSslClientAuthenticationOptions(options)).ConfigureAwait(false);
        return stream;
    }

    public static void SetupMeasurements()
    {
        BenchmarksEventSource.Register("env/processorcount", Operations.First, Operations.First, "Processor Count", "Processor Count", "n0");
        LogMetric("env/processorcount", Environment.ProcessorCount);
    }

    static void RegisterPercentiledMetric(string name, string shortDescription, string longDescription)
    {
        BenchmarksEventSource.Register(name + "/avg", Operations.Min, Operations.Min, shortDescription + " - avg", longDescription + " - avg", "n3");
        BenchmarksEventSource.Register(name + "/min", Operations.Min, Operations.Min, shortDescription + " - min", longDescription + " - min", "n3");
        BenchmarksEventSource.Register(name + "/p50", Operations.Max, Operations.Max, shortDescription + " - p50", longDescription + " - 50th percentile", "n3");
        BenchmarksEventSource.Register(name + "/p75", Operations.Max, Operations.Max, shortDescription + " - p75", longDescription + " - 75th percentile", "n3");
        BenchmarksEventSource.Register(name + "/p90", Operations.Max, Operations.Max, shortDescription + " - p90", longDescription + " - 90th percentile", "n3");
        BenchmarksEventSource.Register(name + "/p99", Operations.Max, Operations.Max, shortDescription + " - p99", longDescription + " - 99th percentile", "n3");
        BenchmarksEventSource.Register(name + "/max", Operations.Max, Operations.Max, shortDescription + " - max", longDescription + " - max", "n3");
    }

    static void LogPercentiledMetric(string name, List<double> values)
    {
        values.Sort();

        LogMetric(name + "/avg", values.Average());
        LogMetric(name + "/min", GetPercentile(0, values));
        LogMetric(name + "/p50", GetPercentile(50, values));
        LogMetric(name + "/p75", GetPercentile(75, values));
        LogMetric(name + "/p90", GetPercentile(90, values));
        LogMetric(name + "/p99", GetPercentile(99, values));
        LogMetric(name + "/max", GetPercentile(100, values));
    }

    static double GetPercentile(int percent, List<double> sortedValues)
    {
        if (percent == 0)
        {
            return sortedValues[0];
        }

        if (percent == 100)
        {
            return sortedValues[sortedValues.Count - 1];
        }

        var i = percent * sortedValues.Count / 100.0 + 0.5;
        var fractionPart = i - Math.Truncate(i);

        return (1.0 - fractionPart) * sortedValues[(int)Math.Truncate(i) - 1] + fractionPart * sortedValues[(int)Math.Ceiling(i) - 1];
    }

    private static void Log(string message)
    {
        var time = DateTime.UtcNow.ToString("hh:mm:ss.fff");
        Console.WriteLine($"[{time}] {message}");
    }

    private static void LogMetric(string name, double value)
    {
        BenchmarksEventSource.Measure(name, value);
        Log($"{name}: {value}");
    }
}