namespace GoldDigger
{
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Linq;
	using System.Net.Http;
	using System.Threading;
	using System.Threading.Tasks;

	public class BlockToExplore
	{
		private volatile int _amount;
		public BlockToExplore(int x, int y, int size, BlockToExplore parent = null)
		{
			X = x;
			Y = y;
			Size = size;
			Parent = parent;
		}

		public int X { get; }
		public int Y { get; }
		public int Size { get; }
		public BlockToExplore Parent { get; }

		public void UpdateAmount(int amount)
		{
			_amount = amount;
			if (Parent != null)
				Interlocked.Add(ref Parent._amount, -amount);
		}

		public bool WorthExploring()
		{
			if (Parent == null) return true;
			var a = Parent._amount;
			if (a <= 0) return false;

			if (Size == 8 && a == 1) return false;

			return true;
		}

		public IEnumerable<BlockToExplore> Break()
		{
			if (Size==1) yield break;

			// can be 16,8,4,2
			for (int dx = 0; dx < Size; dx+=Size/2)
				for (int dy = 0; dy < Size; dy+=Size/2)
					yield return new BlockToExplore(X+dx,Y+dy,Size/2,this);
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
		private readonly CancellationTokenSource _poolCts = new CancellationTokenSource();
		private readonly ConcurrentQueue<int> _coins;

		private readonly Api _api;

		private readonly ConcurrentQueue<int> _freeLicense = new ConcurrentQueue<int>();

		private readonly ConcurrentQueue<int> _paidLicense = new ConcurrentQueue<int>();

		private readonly Task _freeLicensePoll;
		
		private readonly Task _paidLicensePoll;

		private int _longWaits;
		private int _spentOnLicense;

		public LicensePool(Api api, ConcurrentQueue<int> coins)
		{
			_api = api;
			_coins = coins;
			_freeLicensePoll = PollFreeLicense(CancellationToken.None);
			_paidLicensePoll = PollPaidLicense(_poolCts.Token);
		}

		private IEnumerable<int> TakeCoins(int count)
		{
			while (count-- > 0)
			{
				if (_coins.TryDequeue(out var coin))
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
					if (_paidLicense.Count >= 100)
					{
						Interlocked.Increment(ref App._licenserWaitingForRequest);
						await Task.Delay(10);
						continue;
					}

					if (_coins.Count == 0)
					{
						Interlocked.Increment(ref App._licenserWaitingForMoney);
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
						var license = await _api.IssueLicenseAsync(licenseCost, CancellationToken.None);
						if (license != null)
						{
							if (license.digAllowed < 0) // no more license allowed error
							{
								foreach (var coin in licenseCost)
									_coins.Enqueue(coin); // add coins back
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
								_coins.Enqueue(coin); // add coins back
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
						Interlocked.Increment(ref App._licenserWaitingForRequest);
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

		public override string ToString()
		{
			return string.Join('/', new[] {_freeLicense.Count, _paidLicense.Count, _longWaits, _spentOnLicense});
		}

		public void StopPollingPaidLicenses()
		{
			_poolCts.Cancel();
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
					if (_coins.Count > 10000)
					{
						// prefer paid license if we're good
						if (_paidLicense.TryDequeue(out license))
							return (license, false);
					}

					if (_freeLicense.TryDequeue(out license))
						return (license, true);
					if (_paidLicense.TryDequeue(out license))
						return (license, false);

					Interlocked.Increment(ref App._diggersWaitingForAnyLicense);

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
				Interlocked.Increment(ref App._diggersWaitingForPaidLicense);
				await Task.Delay(10);
				if (waitCounter++ > 100)
				{
					Interlocked.Increment(ref _longWaits);
					//Log("Waited more than 1 second for any paid license");
					waitCounter = 0;
					if (DateTime.Now >= App._lastMinute)
					{
						return (int.MinValue, false);
					}
				}
			}

			return (license, false);
		}

	}

	public class App
	{
		private readonly CancellationTokenSource _ctsAppStop = new CancellationTokenSource();

		private readonly ConcurrentQueue<int> _coins = new ConcurrentQueue<int>();
		private readonly List<BlockToExplore> _initialBlocks = new List<BlockToExplore>();
		private volatile int _currentBlock;

		private readonly ConcurrentQueue<TreasureChest> _recoveredTreasures = new ConcurrentQueue<TreasureChest>();
		private readonly ConcurrentQueue<TreasureMap> _treasuresToDig = new ConcurrentQueue<TreasureMap>();
		private readonly ConcurrentQueue<BlockToExplore>[] _secondaryExploreQueue = new ConcurrentQueue<BlockToExplore>[10];

		public static int _explorersWaitingForDiggers = 0;
		public static int _diggersWaitingForCasher = 0;
		public static int _diggersWaitingForMaps = 0;
		public static int _diggersWaitingForPaidLicense = 0;
		public static int _diggersWaitingForAnyLicense = 0;
		public static int _licenserWaitingForRequest = 0;
		public static int _licenserWaitingForMoney = 0;
		public static int _sellerWaitingForTreasure = 0;

		public static DateTime _end;
		public static DateTime _lastMinute;
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
			var address = Environment.GetEnvironmentVariable("ADDRESS");
			var uri = new UriBuilder("http", address ?? "localhost", address == null ? 5000 : 8000).Uri;
			var api = new Api(uri.ToString(), new HttpClient());

			// break down into exploration blocks
			const int blockSize = 16; //218 16 blocks + 3x4
			for (int x = 0; x < 3488; x+= blockSize)
				for (int y = 0; y < 3488; y+= blockSize)
					_initialBlocks.Add(new BlockToExplore(x,y, blockSize));

			for (int x = 3488; x < 3500; x += 4)
				for (int y = 0; y < 3500; y += 4)
					_initialBlocks.Add(new BlockToExplore(x, y, 4));

			for (int x = 0; x < 3488; x += 4)
				for (int y = 3488; y < 3500; y += 4)
					_initialBlocks.Add(new BlockToExplore(x, y, 4));

			foreach (var i in Enumerable.Range(0, 10))
			{
				_secondaryExploreQueue[i] = new ConcurrentQueue<BlockToExplore>();
			}

			Shuffle(_initialBlocks);

			while (!await api.HealthCheckAsync(_ctsAppStop.Token))
			{
				await Task.Delay(10);
			}

			_end = DateTime.Now.AddMinutes(10);
			_lastMinute = _end.AddMinutes(-1);

			_licensePool = new LicensePool(api, _coins);
			
			Log($"Ready - 1000 ticks = {TimeSpan.FromTicks(1000).TotalMilliseconds} msec");

			var tasks = new List<Task>();

			tasks.Add(Explorer(api, true));
			tasks.Add(Explorer(api, false));
			tasks.Add(Explorer(api, false));
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

			bool lastMinuteProcessed = false;
			while (DateTime.Now <= _end)
			{
				await Task.Delay(TimeSpan.FromSeconds(10));
				var stat = api.Snapshot();
				var licPool = _licensePool.ToString();

				int[] waits = new int[8];
				waits[0] = Interlocked.Exchange(ref _explorersWaitingForDiggers, 0);
				waits[1] = Interlocked.Exchange(ref _diggersWaitingForCasher, 0);
				waits[2] = Interlocked.Exchange(ref _diggersWaitingForMaps, 0);
				waits[3] = Interlocked.Exchange(ref _diggersWaitingForPaidLicense, 0);
				waits[4] = Interlocked.Exchange(ref _diggersWaitingForAnyLicense, 0);
				waits[5] = Interlocked.Exchange(ref _licenserWaitingForRequest, 0);
				waits[6] = Interlocked.Exchange(ref _licenserWaitingForMoney, 0);
				waits[7] = Interlocked.Exchange(ref _sellerWaitingForTreasure, 0);

				var cb = _currentBlock > _initialBlocks.Count ? -1 : _currentBlock;
				Log($"lic={stat[0]},exp={stat[1]},dig={stat[2]},cas={stat[3]},pq={cb},sq={string.Join('/', _secondaryExploreQueue.Select(q => q.Count))},maps={_treasuresToDig.Count},tre={_recoveredTreasures.Count},coin={_coins.Count},lic_pool={licPool},waits={string.Join('/', waits)}");

				if (!lastMinuteProcessed && DateTime.Now >= _lastMinute)
				{
					lastMinuteProcessed = true;
					_licensePool.StopPollingPaidLicenses(); // stop paying for licenses during last minute (will eventually stop diggers), exchange remaining treasure
				}
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
						Interlocked.Increment(ref _sellerWaitingForTreasure);
						await Task.Delay(10);
						continue;
					}

					int retry = 0;
					while (true)
					{
						var exchangedCoins = await api.CashAsync(chest.Id, _ctsAppStop.Token);
						if (exchangedCoins != null)
						{
							foreach (var coin in exchangedCoins)
								_coins.Enqueue(coin);
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
				bool lastMinute = false;
				while (!_ctsAppStop.IsCancellationRequested)
				{
					if (!_treasuresToDig.TryDequeue(out var map))
					{
						BlockToExplore block = null;
						foreach (var q in _secondaryExploreQueue)
						{
							if (q.TryDequeue(out block))
							{
								if (block.Size == 2)
									break;
								q.Enqueue(block);
							}
						}

						if (block == null)
						{
							Interlocked.Increment(ref _diggersWaitingForMaps);
							await Task.Delay(10);
							continue;
						}

						block.UpdateAmount(1);
						var oneBlocks = block.Break().ToArray();
						_treasuresToDig.Enqueue(new TreasureMap(oneBlocks[0].X, oneBlocks[0].Y, 1));
						_treasuresToDig.Enqueue(new TreasureMap(oneBlocks[1].X, oneBlocks[1].Y, 1));
						_treasuresToDig.Enqueue(new TreasureMap(oneBlocks[2].X, oneBlocks[2].Y, 1));
						map = new TreasureMap(oneBlocks[3].X, oneBlocks[3].Y, 1); 
					}

					if (_recoveredTreasures.Count > 10000)
					{
						// hold on with digging, we have a long treasure exchange queue
						Interlocked.Increment(ref _diggersWaitingForCasher);
						await Task.Delay(10);
						continue;
					}

					while (map.Amount > 0 && map.Depth <= 9)
					{
						if (map.Depth > 3 && (shallowDigger || _coins.Count == 0 || lastMinute))
						{
							_treasuresToDig.Enqueue(map);
							break; // give up on this treasure
						}

						var (license, free) = await _licensePool.GetLicense(map.Depth);
						if (license == int.MinValue && !free)
						{
							lastMinute = true;
							break; // reached last minute, digging only free
						}

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
					if (DateTime.Now > _lastMinute)
						break;

					if (_treasuresToDig.Count > 10000 && _coins.Count > 10)
					{
						// hold on with exploration, we have a long treasure digging queue
						Interlocked.Increment(ref _explorersWaitingForDiggers);
						await Task.Delay(5);
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
						repeat:
						foreach (var q in _secondaryExploreQueue)
						{
							if (q.TryDequeue(out block))
							{
								if (!block.WorthExploring())
									goto repeat;
								break;
							}
						}

						if (block == null)
						{
							var curBlockIdx = Interlocked.Increment(ref _currentBlock);
							if (curBlockIdx < _initialBlocks.Count)
								block = _initialBlocks[curBlockIdx];

							if (block == null)
							{
								// we explored everything?
								await Task.Delay(10);
								continue;
							}
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
							var a = exploreResult.amount;
							if (a == 0)
								break;

							block.UpdateAmount(a);
							if (block.Size == 1)
							{
								_treasuresToDig.Enqueue(new TreasureMap(block.X, block.Y, a));
							}
							else
							{
								int priority = 0;
								switch (block.Size)
								{
									case 16: priority = 9-Math.Min(a,19)/2; break;
									case 8:  priority = 9-Math.Min(a,9); break;
									case 4:  priority = 3-Math.Min(a,3); break;
									case 2:  priority = a >= 2 ? 0 : 1; break;
								}

								var q = _secondaryExploreQueue[priority];
								foreach (var smallerBlock in block.Break())
									q.Enqueue(smallerBlock);
							}

							break;
						}

						if (retryCount++ > 10)
						{
							var a = block.Size switch
							{
								16 => 10,
								8 => 3,
								_ => 1
							};
							block.UpdateAmount(a);
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
