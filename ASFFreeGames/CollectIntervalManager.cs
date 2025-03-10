﻿using System;
using System.Threading;
using Maxisoft.ASF.Utils;

namespace Maxisoft.ASF;

// The interface that defines the contract for the CollectIntervalManager class
/// <inheritdoc />
/// <summary>
/// An interface that provides methods to manage the collect interval for the ASFFreeGamesPlugin.
/// </summary>
internal interface ICollectIntervalManager : IDisposable {
	/// <summary>
	/// Starts the timer with a random initial and regular delay if it is not already started.
	/// </summary>
	void StartTimerIfNeeded();

	/// <summary>
	/// Changes the collect interval to a new random value and resets the timer.
	/// </summary>
	/// <param name="source">The source object passed to the timer callback.</param>
	/// <returns>The new random collect interval.</returns>
	TimeSpan RandomlyChangeCollectInterval(object? source);

	/// <summary>
	/// Stops the timer and disposes it.
	/// </summary>
	void StopTimer();
}

internal sealed class CollectIntervalManager(IASFFreeGamesPlugin plugin) : ICollectIntervalManager {
	private static readonly RandomUtils.GaussianRandom Random = new();

	/// <summary>
	///     Gets a value that indicates whether to randomize the collect interval or not.
	/// </summary>
	/// <value>
	///     A value of 1 if Options.RandomizeRecheckInterval is true or null, or a value of 0 otherwise.
	/// </value>
	/// <remarks>
	///     This property is used to multiply the standard deviation of the normal distribution used to generate the random delay in the GetRandomizedTimerDelay method. If this property returns 0, then the random delay will be equal to the mean value.
	/// </remarks>
	private int RandomizeIntervalSwitch => plugin.Options.RandomizeRecheckInterval ?? true ? 1 : 0;

	// The timer instance
	private Timer? Timer;

	public void Dispose() => StopTimer();

	// The public method that starts the timer if needed
	public void StartTimerIfNeeded() {
		if (Timer is null) {
			// Get a random initial delay
			TimeSpan initialDelay = GetRandomizedTimerDelay(
				30,
				6 * RandomizeIntervalSwitch,
				1,
				5 * 60
			);

			// Get a random regular delay
			TimeSpan regularDelay = GetRandomizedTimerDelay(
				plugin.Options.RecheckInterval.TotalSeconds,
				7 * 60 * RandomizeIntervalSwitch
			);

			// Create a new timer with the collect operation as the callback
			Timer = new Timer(plugin.CollectGamesOnClock);

			// Start the timer with the initial and regular delays
			Timer.Change(initialDelay, regularDelay);
		}
	}

	/// <summary>
	///     Calculates a random delay using a normal distribution with a mean of Options.RecheckInterval.TotalSeconds and a standard deviation of 7 minutes.
	/// </summary>
	/// <returns>The randomized delay.</returns>
	/// <seealso cref="GetRandomizedTimerDelay(double, double, double, double)" />
	private TimeSpan GetRandomizedTimerDelay() =>
		GetRandomizedTimerDelay(
			plugin.Options.RecheckInterval.TotalSeconds,
			7 * 60 * RandomizeIntervalSwitch
		);

	public TimeSpan RandomlyChangeCollectInterval(object? source) {
		// Calculate a random delay using GetRandomizedTimerDelay method
		TimeSpan delay = GetRandomizedTimerDelay();
		ResetTimer(
			() => new Timer(state => plugin.CollectGamesOnClock(state), source, delay, delay)
		);

		return delay;
	}

	public void StopTimer() => ResetTimer(null);

	/// <summary>
	///     Calculates a random delay using a normal distribution with a given mean and standard deviation.
	/// </summary>
	/// <param name="meanSeconds">The mean of the normal distribution in seconds.</param>
	/// <param name="stdSeconds">The standard deviation of the normal distribution in seconds.</param>
	/// <param name="minSeconds">The minimum value of the random delay in seconds. The default value is 11 minutes.</param>
	/// <param name="maxSeconds">The maximum value of the random delay in seconds. The default value is 1 hour.</param>
	/// <returns>The randomized delay.</returns>
	/// <remarks>
	///     The random number is clamped between the minSeconds and maxSeconds parameters.
	///     This method uses the NextGaussian method from the RandomUtils class to generate normally distributed random numbers.
	///     See [Random nextGaussian() method in Java with Examples] for more details on how to implement NextGaussian in C#.
	/// </remarks>
	private static TimeSpan GetRandomizedTimerDelay(
		double meanSeconds,
		double stdSeconds,
		double minSeconds = 11 * 60,
		double maxSeconds = 60 * 60
	) {
		double randomNumber =
			stdSeconds != 0 ? Random.NextGaussian(meanSeconds, stdSeconds) : meanSeconds;

		TimeSpan delay = TimeSpan.FromSeconds(randomNumber);

		// Convert delay to seconds
		double delaySeconds = delay.TotalSeconds;

		// Clamp the delay between minSeconds and maxSeconds in seconds
		delaySeconds = Math.Max(delaySeconds, minSeconds);
		delaySeconds = Math.Min(delaySeconds, maxSeconds);

		// Convert delay back to TimeSpan
		delay = TimeSpan.FromSeconds(delaySeconds);

		return delay;
	}

	private void ResetTimer(Func<Timer?>? newTimerFactory) {
		Timer?.Dispose();
		Timer = null;

		if (newTimerFactory is not null) {
			Timer = newTimerFactory();
		}
	}
}
