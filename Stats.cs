namespace GoldDigger
{
	using System.Threading;

	public class Stats
	{
		private long _data;
		private long _elapsedSuccess;
		private long _elapsedFails;

		public override string ToString()
		{
			var data = Interlocked.Read(ref _data);

			var elFail = Interlocked.Read(ref _elapsedFails);
			var elSucc = Interlocked.Read(ref _elapsedSuccess);
			var total = (int) (data & 0xFFFFFFFF);
			var fails = (int) (data >> 32);

			var elFailAvg = fails == 0 ? 0 : (int)(elFail / fails);
			var elSuccAvg = total-fails == 0 ? 0 : (int)(elSucc / (total - fails));
			return string.Join('/', new[] { total, fails, elSuccAvg, elFailAvg });
		}

		public void Success(long ticks)
		{
			Interlocked.Add(ref _data, 1);
			Interlocked.Add(ref _elapsedSuccess, ticks);
		}

		public void Fail(long ticks)
		{
			Interlocked.Add(ref _data, 0x0000000100000001);
			Interlocked.Add(ref _elapsedFails, ticks);
		}
	}
}