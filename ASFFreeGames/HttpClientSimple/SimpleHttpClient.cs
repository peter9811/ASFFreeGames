﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Maxisoft.ASF.HttpClientSimple;

#nullable enable

public sealed class SimpleHttpClient : IDisposable {
	private readonly HttpClientHandler HttpClientHandler;
	private readonly HttpClient HttpClient;

	public SimpleHttpClient(IWebProxy? proxy = null, long timeout = 25_000) {
		HttpClientHandler = new HttpClientHandler {
			AutomaticDecompression = DecompressionMethods.All,
			MaxConnectionsPerServer = 5
		};

		SetCheckCertificateRevocationList(HttpClientHandler, true);

		if (proxy is not null) {
			HttpClientHandler.Proxy = proxy;
			HttpClientHandler.UseProxy = true;

			if (proxy.Credentials is not null) {
				HttpClientHandler.PreAuthenticate = true;
			}
		}

#pragma warning disable CA5399
		HttpClient = new HttpClient(HttpClientHandler, false) {
			DefaultRequestVersion = HttpVersion.Version30,
			Timeout = TimeSpan.FromMilliseconds(timeout)
		};
#pragma warning restore CA5399

		SetExpectContinueProperty(HttpClient, false);

		HttpClient.DefaultRequestHeaders.Add("User-Agent", "Lynx/2.8.8dev.9 libwww-FM/2.14 SSL-MM/1.4.1 GNUTLS/2.12.14");
		HttpClient.DefaultRequestHeaders.Add("DNT", "1");
		HttpClient.DefaultRequestHeaders.Add("Sec-GPC", "1");

		HttpClient.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
		HttpClient.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.8));
	}

	public async Task<HttpStreamResponse> GetStreamAsync(Uri uri, IEnumerable<KeyValuePair<string, string>>? additionalHeaders = null, CancellationToken cancellationToken = default) {
		using HttpRequestMessage request = new(HttpMethod.Get, uri);
		request.Version = HttpClient.DefaultRequestVersion;

		// Add additional headers if provided
		if (additionalHeaders != null) {
			foreach (KeyValuePair<string, string> header in additionalHeaders) {
				request.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
		}

		HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		Stream? stream = null;

		try {
			stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception) {
			if (response.IsSuccessStatusCode) {
				throw; // something is wrong
			}

			// assume that the caller checks the status code before reading the stream
		}

		return new HttpStreamResponse(response, stream);
	}

	public void Dispose() {
		HttpClient.Dispose();
		HttpClientHandler.Dispose();
	}

	# region System.MissingMethodException workaround
	private static bool SetCheckCertificateRevocationList(HttpClientHandler httpClientHandler, bool value) {
		try {
			// Get the type of HttpClientHandler
			Type httpClientHandlerType = httpClientHandler.GetType();

			// Get the property information
			PropertyInfo? propertyInfo = httpClientHandlerType.GetProperty("CheckCertificateRevocationList", BindingFlags.Public | BindingFlags.Instance);

			if ((propertyInfo is not null) && propertyInfo.CanWrite) {
				// Set the property value
				propertyInfo.SetValue(httpClientHandler, true);

				return true;
			}
		}
		catch (Exception ex) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericException(ex);
		}

		return false;
	}

	private static bool SetExpectContinueProperty(HttpClient httpClient, bool value) {
		try {
			// Get the DefaultRequestHeaders property
			PropertyInfo? defaultRequestHeadersProperty = httpClient.GetType().GetProperty("DefaultRequestHeaders", BindingFlags.Public | BindingFlags.Instance);

			if (defaultRequestHeadersProperty == null) {
				throw new InvalidOperationException("HttpClient does not have DefaultRequestHeaders property.");
			}

			if (defaultRequestHeadersProperty.GetValue(httpClient) is not HttpRequestHeaders defaultRequestHeaders) {
				throw new InvalidOperationException("DefaultRequestHeaders is null.");
			}

			// Get the ExpectContinue property
			PropertyInfo? expectContinueProperty = defaultRequestHeaders.GetType().GetProperty("ExpectContinue", BindingFlags.Public | BindingFlags.Instance);

			if ((expectContinueProperty != null) && expectContinueProperty.CanWrite) {
				expectContinueProperty.SetValue(defaultRequestHeaders, value);

				return true;
			}
		}
		catch (Exception ex) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericException(ex);
		}

		return false;
	}
	#endregion
}

public sealed class HttpStreamResponse(HttpResponseMessage response, Stream? stream) : IAsyncDisposable {
	public HttpResponseMessage Response { get; } = response;
	public Stream Stream { get; } = stream ?? EmptyStreamLazy.Value;

	public bool HasValidStream => stream is not null && (!EmptyStreamLazy.IsValueCreated || !ReferenceEquals(EmptyStreamLazy.Value, Stream));

	public async Task<string> ReadAsStringAsync(CancellationToken cancellationToken) {
		using StreamReader reader = new(Stream, Encoding.UTF8);

		return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
	}

	public HttpStatusCode StatusCode => Response.StatusCode;

	public async ValueTask DisposeAsync() {
		ConfiguredValueTaskAwaitable task = HasValidStream ? Stream.DisposeAsync().ConfigureAwait(false) : ValueTask.CompletedTask.ConfigureAwait(false);
		Response.Dispose();
		await task;
	}

	private static readonly Lazy<Stream> EmptyStreamLazy = new(static () => new MemoryStream([], false));
}
