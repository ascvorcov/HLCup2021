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
		private readonly ConcurrentQueue<int> _coins;

		private readonly Api _api;

		private static readonly ConcurrentQueue<LicenseWrapper> _freeLicense = new ConcurrentQueue<LicenseWrapper>();

		private static readonly ConcurrentQueue<LicenseWrapper> _paidLicense = new ConcurrentQueue<LicenseWrapper>();

		private static readonly LicenseObject[] _pool = Enumerable.Range(0,10).Select(_ => new LicenseObject()).ToArray(); // max 10 active

		private int _longWaits;
		private int _spentOnLicense;

		public LicensePool(Api api, ConcurrentQueue<int> coins)
		{
			_api = api;
			_coins = coins;
		}

		private IEnumerable<int> TakeCoins(int count)
		{
			while (count-- > 0)
			{
				if (_coins.TryDequeue(out var coin))
					yield return coin;
			}
		}

		public async Task PollPaidLicense(CancellationToken token)
		{
			try
			{
				// price list: 1 coin - 5 digs, 6 coins - 10 digs, 11 coins - 20-29 digs, 21 - 40-49
				while (!token.IsCancellationRequested)
				{
					if (_coins.Count == 0)
					{
						Interlocked.Increment(ref App._licenserWaitingForMoney);
						await Task.Delay(10);
						continue;
					}

					var found = _pool.FirstOrDefault(p => p.TryLock());
					if (found == null)
					{
						Interlocked.Increment(ref App._licenserWaitingForOpenSlot);
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
							Interlocked.Add(ref App._paidLicenseReceivedTotal, license.digAllowed);
							found.Unlock(license.digAllowed);
							foreach (var lic in Enumerable.Repeat(license.id, license.digAllowed))
								_paidLicense.Enqueue(new LicenseWrapper(lic, false, found));
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
		}

		public async Task PollFreeLicense(CancellationToken token)
		{
			try
			{
				while (!token.IsCancellationRequested)
				{
					var found = _pool.FirstOrDefault(p => p.TryLock());
					if (found == null)
					{
						Interlocked.Increment(ref App._licenserWaitingForOpenSlot);
						await Task.Delay(10);
						continue;
					}

					int getLicenseRetryCounter = 0;
					while (true)
					{
						var license = await _api.IssueLicenseAsync(new int[0], CancellationToken.None);

						// App.Log($"Retrieved free license, allows {license.DigAllowed} digs.");
						if (license != null)
						{
							if (license.digAllowed < 0) // no more license allowed error
							{
								await Task.Delay(10);
								break;
							}

							Interlocked.Increment(ref App._freeLicenseReceivedTotal);
							found.Unlock(license.digAllowed);
							foreach (var lic in Enumerable.Repeat(license.id, license.digAllowed))
								_freeLicense.Enqueue(new LicenseWrapper(lic, true, found));
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
		}

		public override string ToString()
		{
			return string.Join('/', new[] {_freeLicense.Count, _paidLicense.Count, _longWaits, _spentOnLicense});
		}

		public async Task<LicenseWrapper> GetLicense(int depth)
		{
			LicenseWrapper license;
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
							return license;
					}

					if (_freeLicense.TryDequeue(out license))
						return license;
					if (_paidLicense.TryDequeue(out license))
						return license;

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
					return null; //maybe worth looking for some other work, requiring free license
				}
			}

			return license;
		}

		public class LicenseObject
		{
			private int _allowed;

			public bool TryLock()
			{
				return Interlocked.CompareExchange(ref _allowed, -1, 0) == 0;
			}

			public void Unlock(int allowed)
			{
				Interlocked.CompareExchange(ref _allowed, allowed, -1);
			}

			public void DecrementAllowed()
			{
				var i = Interlocked.Decrement(ref _allowed);
				if (i < 0) throw new InvalidOperationException("Invalid decrement");
			}
		}

		public class LicenseWrapper
		{
			private bool _free;
			private LicenseObject _parent;
			public LicenseWrapper(int id, bool free, LicenseObject parent)
			{
				Id = id;
				_free = free;
				_parent = parent;
			}

			public int Id;

			public void Return()
			{
				if (_free)
					_freeLicense.Enqueue(this);
				else
					_paidLicense.Enqueue(this);
			}

			public void Discard()
			{
				_parent.DecrementAllowed();
			}
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

		public static int _mapsDiscoveredTotal;
		public static int _treasureDugOutTotal;
		public static int _coinsRetrievedTotal;
		public static int _freeLicenseReceivedTotal;
		public static int _paidLicenseReceivedTotal;

		public static int _explorersWaitingForDiggers;
		public static int _diggersWaitingForCasher;
		public static int _diggersWaitingForMaps;
		public static int _diggersWaitingForPaidLicense;
		public static int _diggersWaitingForAnyLicense;
		public static int _licenserWaitingForOpenSlot;
		public static int _licenserWaitingForMoney;
		public static int _sellerWaitingForTreasure;

		public static DateTime _end;
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

			_licensePool = new LicensePool(api, _coins);
			
			Log($"Ready - 10000 ticks = {TimeSpan.FromTicks(10000).TotalMilliseconds} msec");

			var activeSeekers = new List<(Task,CancellationTokenSource)>();
			var activeDiggers = new List<(Task, CancellationTokenSource)>();
			var activeSellers = new List<(Task, CancellationTokenSource)>();
			var activeFreeLic = new List<(Task, CancellationTokenSource)>();
			var activePaidLic = new List<(Task, CancellationTokenSource)>();

			// for 10 minutes
			var plan = new[]
			{
				// seekers, diggers, sellers, freelic, paidlic
				new[] {12, 2, 0, 2, 0},
				new[] {12, 2, 0, 2, 0},
				new[] {12, 2, 1, 1, 1},
				new[] {8, 12, 1, 1, 3},
				new[] {7, 12, 1, 1, 3},
				new[] {4, 12, 1, 0 ,3},
				new[] {0, 8, 2, 0, 2},
				new[] {0, 6, 4, 0, 2},
				new[] {0, 4, 6, 0, 0},
				new[] {0, 1, 8, 0, 0}
			};

			int secondsPassed = 0;
			while (DateTime.Now <= _end)
			{
				var currentMinute = secondsPassed / 60;
				int seekers = plan[currentMinute][0];
				int diggers = plan[currentMinute][1];
				int sellers = plan[currentMinute][2];
				int freeLic = plan[currentMinute][3];
				int paidLic = plan[currentMinute][4];
				while (seekers != activeSeekers.Count)
				{
					if (seekers > activeSeekers.Count)
					{
						var cts = new CancellationTokenSource();
						activeSeekers.Add((Explorer(api, activeSeekers.Count == 0, cts.Token), cts));
					}
					else
					{
						var last = activeSeekers.Last();
						activeSeekers.RemoveAt(activeSeekers.Count-1);
						last.Item2.Cancel();
					}
				}

				while (diggers != activeDiggers.Count)
				{
					if (diggers > activeDiggers.Count)
					{
						var cts = new CancellationTokenSource();
						activeDiggers.Add((Digger(api, diggers == 2, cts.Token), cts));
					}
					else
					{
						var last = activeDiggers.Last();
						activeDiggers.RemoveAt(activeDiggers.Count-1);
						last.Item2.Cancel();
					}
				}
				
				while (sellers != activeSellers.Count)
				{
					if (sellers > activeSellers.Count)
					{
						var cts = new CancellationTokenSource();
						activeSellers.Add((Seller(api, cts.Token), cts));
					}
					else
					{
						var last = activeSellers.Last();
						activeSellers.RemoveAt(activeSellers.Count-1);
						last.Item2.Cancel();
					}
				}

				while (freeLic != activeFreeLic.Count)
				{
					if (freeLic > activeFreeLic.Count)
					{
						var cts = new CancellationTokenSource();
						activeFreeLic.Add((_licensePool.PollFreeLicense(cts.Token), cts));
					}
					else
					{
						var last = activeFreeLic.Last();
						activeFreeLic.RemoveAt(activeFreeLic.Count - 1);
						last.Item2.Cancel();
					}
				}
				
				while (paidLic != activePaidLic.Count)
				{
					if (paidLic > activePaidLic.Count)
					{
						var cts = new CancellationTokenSource();
						activePaidLic.Add((_licensePool.PollPaidLicense(cts.Token), cts));
					}
					else
					{
						var last = activePaidLic.Last();
						activePaidLic.RemoveAt(activePaidLic.Count - 1);
						last.Item2.Cancel();
					}
				}

				await Task.Delay(TimeSpan.FromSeconds(10));
				secondsPassed += 10;
				var stat = api.Snapshot();
				var licPool = _licensePool.ToString();

				int[] waits = new int[8];
				waits[0] = Interlocked.Exchange(ref _explorersWaitingForDiggers, 0);
				waits[1] = Interlocked.Exchange(ref _diggersWaitingForCasher, 0);
				waits[2] = Interlocked.Exchange(ref _diggersWaitingForMaps, 0);
				waits[3] = Interlocked.Exchange(ref _diggersWaitingForPaidLicense, 0);
				waits[4] = Interlocked.Exchange(ref _diggersWaitingForAnyLicense, 0);
				waits[5] = Interlocked.Exchange(ref _licenserWaitingForOpenSlot, 0);
				waits[6] = Interlocked.Exchange(ref _licenserWaitingForMoney, 0);
				waits[7] = Interlocked.Exchange(ref _sellerWaitingForTreasure, 0);

				var inStream = string.Join('/', new[]
				{
					_mapsDiscoveredTotal,
					_treasureDugOutTotal,
					_coinsRetrievedTotal,
					_freeLicenseReceivedTotal,
					_paidLicenseReceivedTotal
				});

				var cb = _currentBlock > _initialBlocks.Count ? -1 : _currentBlock;
				Log($"lic={stat[0]},exp={stat[1]},dig={stat[2]},cas={stat[3]},pq={cb},stream={inStream},maps={_treasuresToDig.Count},tre={_recoveredTreasures.Count},coin={_coins.Count},lic_pool={licPool},waits={string.Join('/', waits)}");
			}

			_ctsAppStop.Cancel();
			await Task.Delay(10);

			_ctsAppStop.Dispose();
		}

		public async Task Seller(Api api, CancellationToken token)
		{
			try
			{
				while (!token.IsCancellationRequested)
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
							Interlocked.Add(ref _coinsRetrievedTotal, exchangedCoins.Length);
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
		}

		public async Task Digger(Api api, bool shallowDigger, CancellationToken diggerCts)
		{
			try
			{
				while (!diggerCts.IsCancellationRequested)
				{
					if (!_treasuresToDig.TryDequeue(out var map))
					{
						Interlocked.Increment(ref _diggersWaitingForMaps);
						await Task.Delay(10);
						continue;
					}

					while (map.Amount > 0 && map.Depth <= 9)
					{
						if (map.Depth > 3 && (shallowDigger || _coins.Count == 0))
						{
							_treasuresToDig.Enqueue(map);
							break; // give up on this treasure
						}

						var license = await _licensePool.GetLicense(map.Depth);
						if (license == null)
						{
							_treasuresToDig.Enqueue(map);
							break; // give up on this treasure
						}

						int digRetryCounter = 0;
						while (true)
						{
							var dig = new Dig {depth = map.Depth, licenseID = license.Id, posX = map.X, posY = map.Y};
							var treasures = await api.DigAsync(dig, _ctsAppStop.Token);

							if (treasures != null)
							{
								license.Discard();
								foreach (var tr in treasures)
									_recoveredTreasures.Enqueue(new TreasureChest {Id = tr, FromLevel = map.Depth});

								Interlocked.Add(ref _treasureDugOutTotal, treasures.Length);
								map.Depth++;
								map.Amount -= treasures.Length;
								break;
							}

							if (digRetryCounter++ > 10)
							{
								license.Return();
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
		}

		public async Task Explorer(Api api, bool preferPrimaryQueue, CancellationToken token)
		{
			try
			{
				while (!token.IsCancellationRequested)
				{
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
						var exploreResult = await api.ExploreAreaAsync(new Area {posX = block.X, posY = block.Y, sizeX = block.Size, sizeY = block.Size},
							_ctsAppStop.Token);
						if (exploreResult != null)
						{
							var a = exploreResult.amount;
							if (a == 0)
								break;

							block.UpdateAmount(a);
							if (block.Size == 1)
							{
								Interlocked.Increment(ref _mapsDiscoveredTotal);
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
