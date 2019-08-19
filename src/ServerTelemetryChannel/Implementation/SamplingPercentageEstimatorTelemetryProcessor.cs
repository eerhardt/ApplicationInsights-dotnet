﻿namespace Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel.Implementation
{
    using System;
    using System.Threading;

    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;

    /// <summary>
    /// Represents a method that is invoked every time sampling percentage is evaluated
    /// by the dynamic sampling algorithm.
    /// </summary>
    /// <param name="afterSamplingTelemetryItemRatePerSecond">Rate of telemetry items generated by this instance of the application after current sampling percentage was applied.</param>
    /// <param name="currentSamplingPercentage">Current sampling percentage that was used by the algorithm.</param>
    /// <param name="newSamplingPercentage">Suggested new sampling percentage that will allow to keep desired telemetry item generation rate given the volume of items states the same.</param>
    /// <param name="isSamplingPercentageChanged">A value indicating whether new sampling percentage will be applied by dynamic sampling algorithm. New sampling percentage may not be immediately applied in case it was recently changed.</param>
    /// <param name="settings">Dynamic sampling algorithm settings.</param>
    [Obsolete("This was a failed experiment. Please use 'Microsoft.ApplicationInsights.WindowsServer.Channel.Implementation.AdaptiveSamplingPercentageEvaluatedCallback' instead.")]
    public delegate void AdaptiveSamplingPercentageEvaluatedCallback(
       double afterSamplingTelemetryItemRatePerSecond,
       double currentSamplingPercentage,
       double newSamplingPercentage,
       bool isSamplingPercentageChanged,
       Channel.Implementation.SamplingPercentageEstimatorSettings settings);

    /// <summary>
    /// Telemetry processor to estimate ideal sampling percentage.
    /// </summary>
    internal class SamplingPercentageEstimatorTelemetryProcessor : ITelemetryProcessor, IDisposable
    {
        /// <summary>
        /// Next-in-chain processor.
        /// </summary>
        private ITelemetryProcessor next;

        /// <summary>
        /// Dynamic sampling estimator settings.
        /// </summary>
        private Channel.Implementation.SamplingPercentageEstimatorSettings settings;

        /// <summary>
        /// Average telemetry item counter.
        /// </summary>
        private ExponentialMovingAverageCounter itemCount;

        /// <summary>
        /// Average proactively SampledIn telemetry item counter.
        /// </summary>
        private ExponentialMovingAverageCounter proactivelySampledInCount;

        /// <summary>
        /// Evaluation timer.
        /// </summary>
        private Timer evaluationTimer;

        /// <summary>
        /// Current evaluation interval.
        /// </summary>
        private TimeSpan evaluationInterval;

        /// <summary>
        /// Current sampling rate.
        /// </summary>
        private int currenSamplingRate;

        /// <summary>
        /// Last date and time sampling percentage was changed.
        /// </summary>
        private DateTimeOffset samplingPercentageLastChangeDateTime;

        /// <summary>
        /// Callback to invoke every time sampling percentage is evaluated.
        /// </summary>
        private Channel.Implementation.AdaptiveSamplingPercentageEvaluatedCallback evaluationCallback;

        /// <summary>
        /// Initializes a new instance of the <see cref="SamplingPercentageEstimatorTelemetryProcessor"/> class.
        /// <param name="next">Next TelemetryProcessor in call chain.</param>
        /// </summary>
        public SamplingPercentageEstimatorTelemetryProcessor(ITelemetryProcessor next)
            : this(new Channel.Implementation.SamplingPercentageEstimatorSettings(), null, next)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SamplingPercentageEstimatorTelemetryProcessor"/> class.
        /// <param name="settings">Dynamic sampling estimator settings.</param>
        /// <param name="callback">Callback to invoke every time sampling percentage is evaluated.</param>
        /// <param name="next">Next TelemetryProcessor in call chain.</param>
        /// </summary>
        public SamplingPercentageEstimatorTelemetryProcessor(
            Channel.Implementation.SamplingPercentageEstimatorSettings settings, 
            Channel.Implementation.AdaptiveSamplingPercentageEvaluatedCallback callback, 
            ITelemetryProcessor next)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            this.evaluationCallback = callback;
            this.settings = settings;
            this.next = next;

            this.currenSamplingRate = settings.EffectiveInitialSamplingRate;
            this.CurrentProactiveSamplingRate = settings.EffectiveInitialSamplingRate;

            this.itemCount = new ExponentialMovingAverageCounter(settings.EffectiveMovingAverageRatio);
            this.proactivelySampledInCount = new ExponentialMovingAverageCounter(settings.EffectiveMovingAverageRatio);

            this.samplingPercentageLastChangeDateTime = DateTimeOffset.UtcNow;

            // set evaluation interval to default value if it is negative or zero
            this.evaluationInterval = this.settings.EffectiveEvaluationInterval;

            // set up timer to run math to estimate sampling percentage
            this.evaluationTimer = new Timer(
                this.EstimateSamplingPercentage, 
                null,
                this.evaluationInterval,
                this.evaluationInterval);
        }

        /// <summary>
        /// Gets or sets current sampling rate.
        /// </summary>
        internal int CurrentSamplingRate
        {
            get => this.currenSamplingRate;
            set => this.currenSamplingRate = value;
        }

        /// <summary>
        /// Gets current proactive sampling rate sampling rate.
        /// </summary>
        internal double CurrentProactiveSamplingRate { get; private set; }

        /// <summary>
        /// Processes telemetry item.
        /// </summary>
        /// <param name="item">Telemetry item to process.</param>
        public void Process(ITelemetry item)
        {
            // increment post-sampling telemetry item counter
            this.itemCount.Increment();

            if (item is ISupportAdvancedSampling advancedSamplingItem &&
                advancedSamplingItem.ProactiveSamplingDecision == SamplingDecision.SampledIn)
            {
                this.proactivelySampledInCount.Increment();
            }

            // continue processing telemetry item with the next telemetry processor
            this.next.Process(item);
        }

        /// <summary>
        /// Disposes the object.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Checks to see if exponential moving average has changed.
        /// </summary>
        /// <param name="running">Currently running value of moving average.</param>
        /// <param name="current">Value set in the algorithm parameters.</param>
        /// <returns>True if moving average value changed.</returns>
        private static bool MovingAverageCoefficientChanged(double running, double current)
        {
            const double Precision = 1E-12;

            return (running < current - Precision) || (running > current + Precision);
        }

        private void Dispose(bool disposing)
        {
            if (disposing && this.evaluationTimer != null)
            {
                this.evaluationTimer.Dispose();
                this.evaluationTimer = null;
            }
        }

        /// <summary>
        /// Callback for sampling percentage evaluation timer.
        /// </summary>
        /// <param name="state">Timer state.</param>
        private void EstimateSamplingPercentage(object state)
        {
            // get observed after-sampling eps
            double observedEps = this.itemCount.StartNewInterval() / this.evaluationInterval.TotalSeconds;

            // get observed after-sampling eps
            double observedProactiveEps = this.proactivelySampledInCount.StartNewInterval() / this.evaluationInterval.TotalSeconds;

            // we see events post sampling, so get pre-sampling eps
            double beforeSamplingEps = observedEps * this.currenSamplingRate;

            // we see events post sampling, so get pre-sampling eps
            double beforeProactiveSamplingEps = observedProactiveEps * this.CurrentProactiveSamplingRate;

            // calculate suggested sampling rate
            int suggestedSamplingRate = (int)Math.Ceiling(beforeSamplingEps / this.settings.EffectiveMaxTelemetryItemsPerSecond);

            // adjust suggested rate so that it fits between min and max configured
            if (suggestedSamplingRate > this.settings.EffectiveMaxSamplingRate)
            {
                suggestedSamplingRate = this.settings.EffectiveMaxSamplingRate;
            }

            if (suggestedSamplingRate < this.settings.EffectiveMinSamplingRate)
            {
                suggestedSamplingRate = this.settings.EffectiveMinSamplingRate;
            }

            int suggestedProactiveSamplingRate = (int)Math.Ceiling(beforeProactiveSamplingEps / this.settings.EffectiveMaxTelemetryItemsPerSecond);

            // see if evaluation interval was changed and apply change
            if (this.evaluationInterval != this.settings.EffectiveEvaluationInterval)
            {
                this.evaluationInterval = this.settings.EffectiveEvaluationInterval;
                this.evaluationTimer.Change(this.evaluationInterval, this.evaluationInterval);
            }

            // check to see if sampling rate needs changes
            bool samplingPercentageChangeNeeded = suggestedSamplingRate != this.currenSamplingRate;

            if (samplingPercentageChangeNeeded)
            {
                // check to see if enough time passed since last sampling % change
                if ((DateTimeOffset.UtcNow - this.samplingPercentageLastChangeDateTime) <
                    (suggestedSamplingRate > this.currenSamplingRate
                        ? this.settings.EffectiveSamplingPercentageDecreaseTimeout
                        : this.settings.EffectiveSamplingPercentageIncreaseTimeout))
                {
                    samplingPercentageChangeNeeded = false;
                }
            }

            // call evaluation callback if provided
            if (this.evaluationCallback != null)
            {
                // we do not want to crash timer thread knocking out the process
                // in case customer-provided callback failed
                try
                {
                    this.evaluationCallback(
                        observedEps,
                        100.0 / this.currenSamplingRate,
                        100.0 / suggestedSamplingRate,
                        samplingPercentageChangeNeeded,
                        this.settings);
                }
                catch (Exception exp)
                {
                    TelemetryChannelEventSource.Log.SamplingCallbackError(exp.ToString());
                }
            }

            if (samplingPercentageChangeNeeded)
            { 
                // apply sampling percentage change
                this.samplingPercentageLastChangeDateTime = DateTimeOffset.UtcNow;
                this.currenSamplingRate = suggestedSamplingRate;
                this.CurrentProactiveSamplingRate = suggestedProactiveSamplingRate;
            }

            if (samplingPercentageChangeNeeded || 
                MovingAverageCoefficientChanged(this.itemCount.Coefficient, this.settings.EffectiveMovingAverageRatio))
            {
                // since we're observing event count post sampling and we're about
                // to change sampling rate or change coefficient, reset counter
                this.itemCount = new ExponentialMovingAverageCounter(this.settings.EffectiveMovingAverageRatio);
                this.proactivelySampledInCount =
                    new ExponentialMovingAverageCounter(this.settings.EffectiveMovingAverageRatio);
            }
        }
    }
}
