using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GoldDigger
{
	public class BlockToExplore
	{
		public BlockToExplore(int x, int y, int size)
		{
			X = x;
			Y = y;
			Size = size;
		}

		public int X { get; }
		public int Y { get; }
		public int Size { get; }

		public IEnumerable<BlockToExplore> Break()
		{
			// can be 14x14, 2x2, 1x1
			if (Size == 14)
			{
				for (int dx = 0; dx < 14; dx+=2)
					for (int dy = 0; dy < 14; dy+=2)
						yield return new BlockToExplore(X+dx,Y+dy,2);
			}
			else
			if (Size == 2)
			{
				yield return new BlockToExplore(X + 0, Y + 0, 1);
				yield return new BlockToExplore(X + 1, Y + 0, 1);
				yield return new BlockToExplore(X + 0, Y + 1, 1);
				yield return new BlockToExplore(X + 1, Y + 1, 1);
			}
		}
	}

	public class TreasureMap
	{
		public TreasureMap(int x, int y, int amount)
		{
			X = x;
			Y = y;
			Amount = amount;
			Depth = 1;
		}

		public int X { get; }
		public int Y { get; }
		public int Depth { get; set; }
		public int Amount { get; set; }
	}

	public class TreasureChest
	{
		public string Id;
		public int FromLevel;
	}

	public class LicensePool
	{
		private readonly ConcurrentBag<int> _coins;

		private readonly Api _api;

		private readonly ConcurrentQueue<int> _freeLicense = new ConcurrentQueue<int>();

		private readonly ConcurrentQueue<int> _paidLicense = new ConcurrentQueue<int>();

		private readonly Task _freeLicensePoll;
		
		private readonly Task _paidLicensePoll;

		private int _longWaits;
		private int _spentOnLicense;

		public LicensePool(Api api, ConcurrentBag<int> coins, CancellationToken token)
		{
			_api = api;
			_coins = coins;
			_freeLicensePoll = PollFreeLicense(token);
			_paidLicensePoll = PollPaidLicense(token);
		}

		private IEnumerable<int> TakeCoins(int count)
		{
			while (count-- > 0)
			{
				if (_coins.TryTake(out var coin))
					yield return coin;
			}
		}

		private async Task PollPaidLicense(CancellationToken token)
		{
			try
			{
				// price list: 1 coin - 5 digs, 6 coins - 10 digs, 11 coins - 20-29 digs, 21 - 40-49
				while (!token.IsCancellationRequested)
				{
					if (_paidLicense.Count >= 100 || _coins.Count == 0)
					{
						await Task.Delay(10);
						continue;
					}

					var cost = _coins.Count switch
					{
						>= 10000 => 21,
						>= 5000 => 11,
						>= 1000 => 5,
						_ => 1
					};

					var licenseCost = TakeCoins(cost).ToArray();

					int retryCount = 0;
					while (true)
					{
						var license = await _api.IssueLicenseAsync(licenseCost, token);
						if (license != null)
						{
							if (license.digAllowed < 0) // no more license allowed error
							{
								foreach (var coin in licenseCost)
									_coins.Add(coin); // add coins back
								await Task.Delay(10);
								break;
							}

							Interlocked.Add(ref _spentOnLicense,  licenseCost.Length);
							foreach (var lic in Enumerable.Repeat(license.id, license.digAllowed))
								_paidLicense.Enqueue(lic);
							break;
						}

						if (retryCount++ > 10)
						{
							//Log("Something wrong with paid license: " + ex.Message);
							foreach (var coin in licenseCost)
								_coins.Add(coin); // add coins back
							break;
						}
					}
				}
			}
			catch (Exception ex)
			{
				App.Log("poll paid license error:" + ex.Message);
			}

			App.Log("paid license poll completed");
		}

		private async Task PollFreeLicense(CancellationToken token)
		{
			try
			{
				while (!token.IsCancellationRequested)
				{
					if (_freeLicense.Count >= 9)
					{
						await Task.Delay(10);
						continue;
					}

					int getLicenseRetryCounter = 0;
					while (true)
					{
						var license = await _api.IssueLicenseAsync(new int[0], token);

						// App.Log($"Retrieved free license, allows {license.DigAllowed} digs.");
						if (license != null)
						{
							if (license.digAllowed < 0) // no more license allowed error
							{
								await Task.Delay(10);
								break;
							}

							foreach (var lic in Enumerable.Repeat(license.id, license.digAllowed))
								_freeLicense.Enqueue(lic);
							break;
						}

						if (getLicenseRetryCounter++ > 10)
							break;
					}
				}
			}
			catch (Exception ex)
			{
				App.Log("poll free license error:" + ex.Message);
			}

			App.Log("free license poll completed");
		}

		public (int free, int paid, int waits, int spent) Snapshot()
		{
			return (_freeLicense.Count, _paidLicense.Count, _longWaits, _spentOnLicense);
		}

		public void ReturnLicense(int license, bool free)
		{
			if (free)
				_freeLicense.Enqueue(license);
			else
				_paidLicense.Enqueue(license);
		}

		public async Task<(int license, bool free)> GetLicense(int depth)
		{
			int license;
			int waitCounter = 0;
			if (depth <= 3)
			{
				// for small depth, free license is fine.
				// but it is also ok to use paid license
				while (true)
				{
					if (_coins.Count > 1000)
					{
						// prefer paid license if we're good
						if (_paidLicense.TryDequeue(out license))
							return (license, false);
					}

					if (_freeLicense.TryDequeue(out license))
						return (license, true);
					if (_paidLicense.TryDequeue(out license))
						return (license, false);

					await Task.Delay(10);
					if (waitCounter++ > 100)
					{
						Interlocked.Increment(ref _longWaits);
						//Log("Waited more than 1 second for any license");
						waitCounter = 0;
					}
				}
			}

			// only paid license will do the trick
			while (!_paidLicense.TryDequeue(out license))
			{
				await Task.Delay(10);
				if (waitCounter++ > 100)
				{
					Interlocked.Increment(ref _longWaits);
					//Log("Waited more than 1 second for any paid license");
					waitCounter = 0;
				}
			}

			return (license, false);
		}

	}

	public class App
	{
		private readonly CancellationTokenSource _ctsAppStop = new CancellationTokenSource();

		private readonly ConcurrentBag<int> _coins = new ConcurrentBag<int>();
		private readonly List<BlockToExplore> _initialBlocks = new List<BlockToExplore>();
		private volatile int _currentBlock;

		private readonly ConcurrentQueue<TreasureChest> _recoveredTreasures = new ConcurrentQueue<TreasureChest>();
		private readonly ConcurrentQueue<TreasureMap> _treasuresToDig = new ConcurrentQueue<TreasureMap>();
		private readonly ConcurrentQueue<BlockToExplore>[] _secondaryExploreQueue = new ConcurrentQueue<BlockToExplore>[10];
		private readonly int[] _levelYield = new int[10];
		private LicensePool _licensePool;

		public static async Task Main()
		{
			Log("Start");
			await new App().Run();
		}

		public static void Log(string message)
		{
			Console.Write(DateTime.Now);
			Console.Write(": ");
			Console.WriteLine(message);
		}

		private async Task Run()
		{
			var end = DateTime.Now.AddMinutes(11);

			await Task.Delay(10);
			var address = Environment.GetEnvironmentVariable("ADDRESS");
			var uri = new UriBuilder("http", address ?? "localhost", address == null ? 5000 : 8000).Uri;
			var api = new Api(uri.ToString(), new HttpClient());

			// break down into exploration blocks
			const int blockSize = 14;
			for (int x = 0; x < 3500; x+= blockSize)
			{
				for (int y = 0; y < 3500; y+= blockSize)
				{
					_initialBlocks.Add(new BlockToExplore(x,y, blockSize));
				}
			}

			foreach (var i in Enumerable.Range(0, 10))
			{
				_secondaryExploreQueue[i] = new ConcurrentQueue<BlockToExplore>();
			}

			Shuffle(_initialBlocks);

			while (!await api.HealthCheckAsync(_ctsAppStop.Token))
			{
				await Task.Delay(10);
			}

			_licensePool = new LicensePool(api, _coins, _ctsAppStop.Token);

			Log("Ready");

			var tasks = new List<Task>();

			tasks.Add(Explorer(api, true));
			tasks.Add(Explorer(api, false));
			tasks.Add(Explorer(api, false));
			tasks.Add(Explorer(api, false));
			tasks.Add(Digger(api, true));
			tasks.Add(Digger(api, true));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Seller(api));
			tasks.Add(Seller(api));
			tasks.Add(Seller(api));

			while (DateTime.Now < end)
			{
				await Task.Delay(TimeSpan.FromSeconds(10));
				var lic = api.Stats[0].Snapshot();
				var exp = api.Stats[1].Snapshot();
				var dig = api.Stats[2].Snapshot();
				var cas = api.Stats[3].Snapshot();
				var licPool = _licensePool.Snapshot();
				
				var cb = _currentBlock > _initialBlocks.Count ? -1 : _currentBlock;
				Log($"lic={lic},exp={exp},dig={dig},cas={cas},pq={cb},sq={string.Join('/', _secondaryExploreQueue.Select(q => q.Count))},maps={_treasuresToDig.Count},tre={_recoveredTreasures.Count},coin={_coins.Count},yld={string.Join('/', _levelYield)},lic_pool={licPool}");
			}

			tasks.Clear();
			_ctsAppStop.Cancel();
			await Task.Delay(10);

			_ctsAppStop.Dispose();
		}

		public async Task Seller(Api api)
		{
			try
			{
				while (!_ctsAppStop.IsCancellationRequested)
				{
					if (!_recoveredTreasures.TryDequeue(out var chest))
					{
						await Task.Delay(10);
						continue;
					}

					int retry = 0;
					while (true)
					{
						var exchangedCoins = await api.CashAsync(chest.Id, _ctsAppStop.Token);
						if (exchangedCoins != null)
						{
							// level yield in coins
							_levelYield[chest.FromLevel - 1] += exchangedCoins.Length;
							foreach (var coin in exchangedCoins)
								_coins.Add(coin);
							break;
						}

						if (++retry > 10)
						{
							_recoveredTreasures.Enqueue(chest); // give up
							break;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Log("seller error:" + ex.Message);
			}

			Log("seller completed");
		}

		public async Task Digger(Api api, bool shallowDigger = false)
		{
			try
			{
				while (!_ctsAppStop.IsCancellationRequested)
				{
					if (!_treasuresToDig.TryDequeue(out var map))
					{
						await Task.Delay(10);
						continue;
					}

					if (_recoveredTreasures.Count > 1000)
					{
						// hold on with digging, we have a long treasure exchange queue
						await Task.Delay(10);
						continue;
					}

					while (map.Amount > 0 && map.Depth <= 6)
					{
						if (map.Depth > 3 && (shallowDigger || _coins.Count == 0))
						{
							_treasuresToDig.Enqueue(map);
							break; // give up on this treasure
						}

						var (license, free) = await _licensePool.GetLicense(map.Depth);

						int digRetryCounter = 0;
						while (true)
						{
							var dig = new Dig {depth = map.Depth, licenseID = license, posX = map.X, posY = map.Y};
							var treasures = await api.DigAsync(dig, _ctsAppStop.Token);

							if (treasures != null)
							{
								if (treasures.Length > 1)
									Log($"Dug out {treasures.Length} treasures from level {map.Depth}");
								foreach (var tr in treasures)
									_recoveredTreasures.Enqueue(new TreasureChest {Id = tr, FromLevel = map.Depth});

								map.Depth++;
								map.Amount -= treasures.Length;
								break;
							}

							if (digRetryCounter++ > 10)
							{
								_licensePool.ReturnLicense(license, free);
								_treasuresToDig.Enqueue(map);
								break; // give up on this treasure
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				Log("digger error:" + ex.Message);
			}

			Log("digger completed");
		}

		public async Task Explorer(Api api, bool preferPrimaryQueue)
		{
			try
			{
				while (!_ctsAppStop.IsCancellationRequested)
				{
					if (_treasuresToDig.Count > 1000)
					{
						// hold on with exploration, we have a long treasure digging queue
						await Task.Delay(10);
						continue;
					}

					BlockToExplore block = null;
					if (preferPrimaryQueue)
					{
						var curBlockIdx = Interlocked.Increment(ref _currentBlock);
						if (curBlockIdx < _initialBlocks.Count)
							block = _initialBlocks[curBlockIdx];
					}

					if (block == null)
					{
						foreach (var q in _secondaryExploreQueue)
						{
							if (q.TryDequeue(out block))
								break;
						}

						if (block == null)
						{
							await Task.Delay(10);
							continue;
						}
					}

					int retryCount = 0;
					while (true)
					{
						var exploreResult = await api.ExploreAreaAsync(
							new Area {posX = block.X, posY = block.Y, sizeX = block.Size, sizeY = block.Size},
							_ctsAppStop.Token);
						if (exploreResult != null)
						{
							if (exploreResult.amount == 0)
								break;

							if (block.Size == 1)
								_treasuresToDig.Enqueue(new TreasureMap(block.X, block.Y, exploreResult.amount));
							else if (block.Size == 14)
							{
								if (exploreResult.amount > 18) exploreResult.amount = 18;
								var priority = 9 - exploreResult.amount / 2;

								foreach (var smallerBlock in block.Break())
									_secondaryExploreQueue[priority].Enqueue(smallerBlock);
							}
							else if (block.Size == 2)
							{
								var priority = exploreResult.amount > 2 ? 0 : 1;
								foreach (var smallestBlock in block.Break())
									_secondaryExploreQueue[priority].Enqueue(smallestBlock);
							}

							break;
						}

						if (retryCount++ > 10)
						{
							foreach (var smallerBlock in block.Break())
								_secondaryExploreQueue[5].Enqueue(smallerBlock);
							break; // give up block
						}
					}
				}
			}
			catch (Exception ex)
			{
				Log("explorer error:" + ex.Message);
			}

			Log("explorer completed");
		}

		public static void Shuffle<T>(IList<T> list)
		{
			var rnd = new Random();
			int n = list.Count;
			while (n > 1)
			{
				n--;
				int k = rnd.Next(n + 1);
				T value = list[k];
				list[k] = list[n];
				list[n] = value;
			}
		}
	}
}
