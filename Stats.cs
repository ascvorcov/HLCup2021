using System.Threading;

namespace GoldDigger
{
	public class Stats
	{
		private long _data;

		public (int requestsTotal, int failures) Snapshot()
		{
			var data = Interlocked.Read(ref _data);

			return ((int) (data & 0xFFFFFFFF), (int) (data >> 32));
		}

		public void Success()
		{
			Interlocked.Add(ref _data, 1);
		}

		public void Fail()
		{
			Interlocked.Add(ref _data, 0x0000000100000001);
		}
	}
}