﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using Maxisoft.ASF.HttpClientSimple;
using Maxisoft.ASF.Reddit;
using Maxisoft.ASF.Redlib;
using Maxisoft.ASF.Redlib.Html;
using Maxisoft.ASF.Redlib.Instances;

// ReSharper disable once CheckNamespace
namespace Maxisoft.ASF.FreeGames.Strategies;

[SuppressMessage("ReSharper", "RedundantNullableFlowAttribute")]
public sealed class RedlibListFreeGamesStrategy : IListFreeGamesStrategy {
	private readonly SemaphoreSlim DownloadSemaphore = new(4, 4);
	private readonly CachedRedlibInstanceListStorage InstanceListCache = new(
		Array.Empty<Uri>(),
		DateTimeOffset.MinValue
	);

	public void Dispose() => DownloadSemaphore.Dispose();

	public async Task<IReadOnlyCollection<RedditGameEntry>> GetGames(
		[NotNull] ListFreeGamesContext context,
		CancellationToken cancellationToken
	) {
		cancellationToken.ThrowIfCancellationRequested();

		CachedRedlibInstanceList instanceList = new(context.Options, InstanceListCache);

		List<Uri> instances = await instanceList
			.ListInstances(context.HttpClientFactory.CreateForGithub(), cancellationToken)
			.ConfigureAwait(false);
		instances = Shuffle(instances);
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken
		);
		cts.CancelAfter(60_000);

		LinkedList<Task<IReadOnlyCollection<RedditGameEntry>>> tasks = [];
		Task<IReadOnlyCollection<RedditGameEntry>>[] allTasks = [];

		try {
			foreach (Uri uri in instances) {
				tasks.AddLast(
					DownloadUsingInstance(context.HttpClient.Value, uri, context.Retry, cts.Token)
				);
			}

			allTasks = tasks.ToArray();
			IReadOnlyCollection<RedditGameEntry> result = await MonitorDownloads(tasks, cts.Token)
				.ConfigureAwait(false);

			if (result.Count > 0) {
				return result;
			}
		}
		finally {
			await cts.CancelAsync().ConfigureAwait(false);

			try {
				await Task.WhenAll(tasks).ConfigureAwait(false);
			}
			catch (Exception) {
				// ignored
			}

			foreach (Task<IReadOnlyCollection<RedditGameEntry>> task in allTasks) {
				task.Dispose();
			}
		}

		List<Exception> exceptions = new(allTasks.Length);
		exceptions.AddRange(
			from task in allTasks
			where task.IsCanceled || task.IsFaulted
			select IListFreeGamesStrategy.ExceptionFromTask(task)
		);

		switch (exceptions.Count) {
			case 1:
				throw exceptions[0];
			case > 0:
				throw new AggregateException(exceptions);
			default:
				cts.Token.ThrowIfCancellationRequested();

				throw new InvalidOperationException("This should never happen");
		}
	}

	/// <summary>
	///     Tries to get the date from the HTTP headers using reflection.
	/// </summary>
	/// <param name="response">The HTTP response.</param>
	/// <returns>The date from the HTTP headers, or null if not found.</returns>
	/// <remarks>
	///     This method is used to work around the trimmed binary issue in the release build.
	///     In the release build, the <see cref="HttpResponseMessage.Headers" /> property is trimmed, and the <c>Date</c>
	///     property is not available. This method uses reflection to safely try to get the date from the HTTP headers.
	/// </remarks>
	public static DateTimeOffset? GetDateFromHeaders([NotNull] HttpResponseMessage response) {
		try {
			Type headersType = response.Headers.GetType();

			// Try to get the "Date" property using reflection
			PropertyInfo? dateProperty = headersType.GetProperty("Date");

			if (dateProperty != null) {
				// Get the value of the "Date" property
				object? dateValue = dateProperty.GetValue(response.Headers);

				// Check if the value is of type DateTimeOffset?
				if (dateValue is DateTimeOffset?) {
					return (DateTimeOffset?) dateValue;
				}
			}
		}
		catch (Exception) {
			// ignored
		}

		return null;
	}

	private async Task<IReadOnlyCollection<RedditGameEntry>> DoDownloadUsingInstance(
		SimpleHttpClient client,
		Uri uri,
		CancellationToken cancellationToken
	) {
		await DownloadSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
		string content;
		DateTimeOffset date = default;

		try {
#pragma warning disable CAC001
#pragma warning disable CA2007
			await using HttpStreamResponse resp = await client
				.GetStreamAsync(uri, cancellationToken: cancellationToken)
				.ConfigureAwait(false);
#pragma warning restore CA2007
#pragma warning restore CAC001

			if (!resp.HasValidStream) {
				throw new HttpRequestRedlibException("invalid stream for " + uri) {
					Uri = uri,
					StatusCode = resp.StatusCode,
				};
			}
			else if (!resp.StatusCode.IsSuccessCode()) {
				throw new HttpRequestRedlibException(
					$"invalid status code {resp.StatusCode} for {uri}"
				) {
					Uri = uri,
					StatusCode = resp.StatusCode,
				};
			}
			else {
				content = await resp.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

				date = GetDateFromHeaders(resp.Response) ?? date;
			}
		}
		finally {
			DownloadSemaphore.Release();
		}

		IReadOnlyCollection<RedlibGameEntry> entries = RedlibHtmlParser.ParseGamesFromHtml(content);
		DateTimeOffset now = DateTimeOffset.Now;

		if ((date == default(DateTimeOffset)) || ((now - date).Duration() > TimeSpan.FromDays(1))) {
			date = now;
		}

		long dateMillis = date.ToUnixTimeMilliseconds();

		List<RedditGameEntry> redditGameEntries = [];

		// ReSharper disable once LoopCanBeConvertedToQuery
		foreach (RedlibGameEntry entry in entries) {
			redditGameEntries.Add(entry.ToRedditGameEntry(dateMillis));
		}

		return redditGameEntries;
	}

	private async Task<IReadOnlyCollection<RedditGameEntry>> DownloadUsingInstance(
		SimpleHttpClient client,
		Uri uri,
		uint retry,
		CancellationToken cancellationToken
	) {
		Uri fullUrl = new(
			$"{uri.ToString().TrimEnd('/')}/user/{RedditHelper.User}?sort=new",
			UriKind.Absolute
		);

		for (int t = 0; t < retry; t++) {
			try {
				return await DoDownloadUsingInstance(client, fullUrl, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception) {
				if ((t == retry - 1) || cancellationToken.IsCancellationRequested) {
					throw;
				}

				await Task.Delay(1000 * (1 << t), cancellationToken).ConfigureAwait(false);
			}
		}

		cancellationToken.ThrowIfCancellationRequested();

		throw new InvalidOperationException("This should never happen");
	}

	private static async Task<IReadOnlyCollection<RedditGameEntry>> MonitorDownloads(
		LinkedList<Task<IReadOnlyCollection<RedditGameEntry>>> tasks,
		CancellationToken cancellationToken
	) {
		while (tasks.Count > 0) {
			cancellationToken.ThrowIfCancellationRequested();

			try {
				await Task.WhenAny(tasks).ConfigureAwait(false);
			}
			catch (Exception) {
				//ignored
			}

			LinkedListNode<Task<IReadOnlyCollection<RedditGameEntry>>>? node = tasks.First;

			while (node is not null) {
				Task<IReadOnlyCollection<RedditGameEntry>> task = node.Value;

				if (task.IsCompletedSuccessfully) {
					IReadOnlyCollection<RedditGameEntry> result = await task.ConfigureAwait(false);

					if (result.Count > 0) {
						return result;
					}
				}

				if (task.IsCompleted) {
					tasks.Remove(node);
					node = tasks.First;
					task.Dispose();

					continue;
				}

				node = node.Next;
			}
		}

		return [];
	}

	/// <summary>
	///     Shuffles a list of URIs. <br />
	///     This is done using a non performant guids generation for asf trimmed binary compatibility.
	/// </summary>
	/// <param name="list">The list of URIs to shuffle.</param>
	/// <returns>A shuffled list of URIs.</returns>
	private static List<Uri> Shuffle<TCollection>(TCollection list)
		where TCollection : ICollection<Uri> {
		List<(Guid, Uri)> randomized = new(list.Count);
		randomized.AddRange(list.Select(static uri => (Guid.NewGuid(), uri)));

		randomized.Sort(static (x, y) => x.Item1.CompareTo(y.Item1));

		return randomized.Select(static x => x.Item2).ToList();
	}
}
