﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Reflection;
using Vixen.Common;
// Using System.Timers.Timer because it exposes a SynchronizingObject member that lets
// you specify the thread context for the Elapsed event.
using System.Timers;
using Vixen.Hardware;
using System.Threading;
using System.Threading.Tasks;
using Vixen.Sys;
using Vixen.Module.Output;
using Vixen.Module.RuntimeBehavior;
using Vixen.Module.Timing;
using Vixen.Module.Media;

namespace Vixen.Execution {
	class SequenceExecutor : IExecutor, IDisposable {
		private System.Timers.Timer _updateTimer;
		private ISequence _sequence;
		private IRuntimeBehaviorModuleInstance[] _runtimeBehaviors;
		private IComparer<CommandNode> _commandNodeComparer = new CommandNode.Comparer();
		private ExecutorEffectEnumerator _sequenceDataEnumerator;

		public event EventHandler<SequenceStartedEventArgs> SequenceStarted;
		public event EventHandler SequenceEnded;
		public event EventHandler<ExecutorMessageEventArgs> Message;
		public event EventHandler<ExecutorMessageEventArgs> Error;

		// Public for Activator.
		public SequenceExecutor() {
			_updateTimer = new System.Timers.Timer(10);
			_updateTimer.Elapsed += _UpdateTimerElapsed;
		}

		public ISequence Sequence {
			get { return _sequence; }
			set {
				if(_sequence != value) {
					_sequence = value;

					// Get runtime behavior.
					_runtimeBehaviors = value.RuntimeBehaviors;
				}
			}
		}

		private bool _DataListener(CommandNode commandNode) {
			// Data has been inserted into the sequence.
			// Give every behavior a chance at the data.
			foreach(IRuntimeBehavior behavior in _runtimeBehaviors) {
				if(behavior.Enabled) {
					behavior.Handle(commandNode);
				}
			}

			// Data written to a sequence will go through the behaviors and then on to the
			// effect enumerator of the executor by way of the CommandNodeIntervalSync
			// to be executed against the sequence's time.  This has the side effect of
			// allowing timed-live behavior without an explicit runtime behavior that has
			// to manage timing on its own.
			// Note: Data written to the entry point being used here is *not* synced with
			// sequence interval timing.  It could be, using another entry point.
			_sequence.Data.AddLive(commandNode);

			// We don't want any handlers beyond the executor to get live data.
			return true;
		}

		public void Play(long startTime, long endTime) {
			if(this.Sequence != null) {
				// Only hook the input stream during execution.
				// Hook before starting the behaviors.
				_sequence.InsertDataListener += _DataListener;

				// Bound the execution range.
				StartTime = Math.Min(startTime, this.Sequence.Length);
				EndTime = Math.Min(endTime, this.Sequence.Length);

				// Notify any subclass that we're ready to start and allow it to do
				// anything it needs to prepare.
				OnPlaying(StartTime, EndTime);
				
				TimingSource = this.Sequence.TimingProvider.GetSelectedSource() ??
					Modules.GetModuleManager<ITimingModuleInstance, TimingModuleManagement>().GetDefault();

				// Initialize behaviors BEFORE data is pulled from the sequence,
				// they may influence the data.
				foreach(IRuntimeBehavior behavior in _runtimeBehaviors) {
					behavior.Startup(this.Sequence, TimingSource);
				}

				// CommandNodes that have any intervals within the time frame.
				var qualifiedData = this.Sequence.Data.GetCommandRange(StartTime, EndTime);
					// Done by GetCommandRange now.  Otherwise, trying to get an enumerator
					// for the collection will not the be enumerator we intend.
					//.OrderBy(x => x.StartTime);
				// Get the qualified sequence data into an enumerator.
				_sequenceDataEnumerator = new ExecutorEffectEnumerator(qualifiedData, TimingSource, StartTime, EndTime);

				// Load the media.
				foreach(IMediaModuleInstance media in Sequence.Media) {
					media.LoadMedia(StartTime);
				}

				// Data generation is dependent upon the timing source, so wait to start it
				// until all potention sources of timing (timing modules and media right
				// now) are loaded.
				_StartDataGeneration();

				// Start the crazy train.
				IsRunning = true;
				OnSequenceStarted(new SequenceStartedEventArgs(TimingSource));

				// Start the media.
				foreach(IMediaModuleInstance media in Sequence.Media) {
					media.Start();
				}
				TimingSource.Position = StartTime;
				TimingSource.Start();
				
				// Fire the first event manually because it takes a while for the timer
				// to elapse the first time.
				_UpdateOutputs();
				// If there is no length, we may have been stopped as a cascading result
				// of that update.
				if(IsRunning) {
					_updateTimer.Start();
				}
			}
		}

		private void _StartDataGeneration() {
			// Start the data generation.
			Thread thread = new Thread(_DataGenerationThread);
			thread.Name = "DataGeneration-" + Sequence.Name;
			thread.IsBackground = true;
			thread.Start();
		}

		private void _DataGenerationThread() {
			// We are going to use IsRunning to tell us when to stop running, but we want
			// to get a head start so we're going to start running before IsRunning is
			// set.  Therefore, we have to watch for the set->reset transition to know
			// when to stop.
			bool transitionToSet = false;
			bool transitionToReset = false;
			List<CommandNode> qualifiedCommands = new List<CommandNode>();

			do {
				if(IsRunning) transitionToSet = true;
				if(transitionToSet && !IsRunning) transitionToReset = true;

				//*** may be faster to create a new one
				qualifiedCommands.Clear();

				// Get everything that currently qualifies.
				while(_sequenceDataEnumerator.MoveNext()) {
					qualifiedCommands.Add(_sequenceDataEnumerator.Current);
				}

				// Execute it as a single state.
				if(qualifiedCommands.Count > 0) {
					Vixen.Sys.Execution.Write(qualifiedCommands);
				}

				//completely arbitrary...
				Thread.Sleep(5);
			} while(!transitionToReset);
		}

		virtual protected void OnPlaying(long startTime, long endTime) { }

		public void Pause() {
			if(_updateTimer.Enabled) {
				TimingSource.Pause();
				foreach(IMediaModuleInstance media in Sequence.Media) {
					media.Pause();
				}
				OutputController.PauseControllers();
				OnPausing();
				_updateTimer.Enabled = false;
			}
		}

		virtual protected void OnPausing() { }

		public void Resume() {
			if(!_updateTimer.Enabled && this.Sequence != null) {
				TimingSource.Resume();
				foreach(IMediaModuleInstance media in Sequence.Media) {
					media.Resume();
				}
				OutputController.ResumeControllers();
				_updateTimer.Enabled = true;
				OnResumed();
			}
		}

		virtual protected void OnResumed() { }

		public void Stop() {
			if(IsRunning) {
				_Stop();
			}
		}

		private void _Stop() {
			// Stop whatever is driving this crazy train.
			lock(_updateTimer) {
				_updateTimer.Enabled = false;
			}

			// Notify the world.
			OnStopping();

			// Release the hook before the behaviors are shut down so that
			// they can affect the sequence.
			_sequence.InsertDataListener -= _DataListener;

			// Shutdown the behaviors.
			foreach(IRuntimeBehavior behavior in _runtimeBehaviors) {
				behavior.Shutdown();
			}

			IsRunning = false;

			OnSequenceEnded(EventArgs.Empty);

			TimingSource.Stop();
			foreach(IMediaModuleInstance media in Sequence.Media) {
				media.Stop();
			}
		}

		virtual protected void OnStopping() { }

		public bool IsRunning { get; private set; }

		private void _UpdateTimerElapsed(object sender, ElapsedEventArgs e) {
			lock(_updateTimer) {
				// To catch events that may trail after the timer's been disabled
				// due to it being a threaded timer and Stop being called between the
				// timer message being posted and acted upon.
				if(_updateTimer == null || !_updateTimer.Enabled) return;

				_updateTimer.Enabled = false;

				_UpdateOutputs();

				// Cannot do _updateTimer.Enabled = IsRunning because _updateTimer is null
				if(IsRunning) {
					_updateTimer.Enabled = true;
				}
			}
		}

		private void _UpdateOutputs() {
			if(_IsEndOfSequence()) {
				Stop();
			}
		}

		private bool _IsEndOfSequence() {
			return EndTime >= StartTime && TimingSource.Position >= EndTime;
		}

		protected virtual void OnSequenceStarted(SequenceStartedEventArgs e) {
			if(SequenceStarted != null) {
				SequenceStarted(this.Sequence, e);
			}
		}

		protected virtual void OnSequenceEnded(EventArgs e) {
			if(SequenceEnded != null) {
				SequenceEnded(this.Sequence, e);
			}
		}

		protected virtual void OnMessage(ExecutorMessageEventArgs e) {
			if(Message != null) {
				Message(this.Sequence, e);
			}
		}

		protected virtual void OnError(ExecutorMessageEventArgs e) {
			if(Error != null) {
				Error(this.Sequence, e);
			}
		}

		// Because these are calculated values, changing the length of the sequence
		// during execution will not affect the end time.
		public long StartTime { get; protected set; }
		public long EndTime { get; protected set; }

		static public IExecutor GetExecutor(ISequence executable) {
			Type attributeType = typeof(ExecutorAttribute);
			IExecutor executor = null;
			// If the executable is decorated with [Executor], get that executor.
			// Since sequences are implemented as modules now, we need to look in the inheritance chain
			// for the attribute.
			ExecutorAttribute attribute = (ExecutorAttribute)executable.GetType().GetCustomAttributes(attributeType, true).FirstOrDefault();
			if(attribute != null) {
				// Create the executor.
				executor = Activator.CreateInstance(attribute.ExecutorType) as IExecutor;
				// Assign the sequence to the executor.
				executor.Sequence = executable;
			}
			return executor;
		}

		~SequenceExecutor() {
			Dispose(false);
		}

		public void Dispose() {
			Dispose(true);
		}

		public virtual void Dispose(bool disposing) {
			if(_updateTimer != null) {
				lock(_updateTimer) {
					Stop();
					_updateTimer.Elapsed -= _UpdateTimerElapsed;
					_updateTimer.Dispose();
					_updateTimer = null;
				}
			}
			GC.SuppressFinalize(this);
		}

		#region Timing implementation

		protected ITiming TimingSource { get; set; }

		#endregion
	}

}