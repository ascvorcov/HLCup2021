namespace GoldDigger
{
	using System.Diagnostics;

	public class Stats
	{
		public int RequestCount;
		public int FailureCount;
		public long TimeSpent;

		public Tracker Begin()
		{
			return new Tracker(this);
		}

		public Stats Snapshot()
		{
			lock (this)
			{
				var ret = new Stats
					{RequestCount = this.RequestCount, FailureCount = this.FailureCount, TimeSpent = this.TimeSpent};
				this.RequestCount = 0;
				this.FailureCount = 0;
				this.TimeSpent = 0;
				return ret;
			}
		}

		public class Tracker
		{
			private readonly Stats parent;
			public readonly Stopwatch sw = Stopwatch.StartNew();

			public Tracker(Stats parent)
			{
				this.parent = parent;
			}

			public void Success()
			{
				var elapsed = sw.ElapsedTicks;
				lock (this.parent)
				{
					this.parent.RequestCount++;
					this.parent.TimeSpent += elapsed;
				}
			}

			public void Fail()
			{
				var elapsed = sw.ElapsedTicks;
				lock (this.parent)
				{
					this.parent.RequestCount++;
					this.parent.FailureCount++;
					this.parent.TimeSpent += elapsed;
				}
			}
		}
	}
}