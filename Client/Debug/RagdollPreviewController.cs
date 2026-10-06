using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.Game.Spawning;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;
using UnityEngine.AI;

namespace RagdollKinetics
{
    internal sealed class RagdollPreviewController : MonoBehaviour
    {
        private static readonly FieldInfo GameEndField =
            AccessTools.Field(typeof(BotSpawner), "_gameEnd");
        private static readonly FieldInfo InSpawnProcessField =
            AccessTools.Field(typeof(BotSpawner), "_inSpawnProcess");
        private static readonly FieldInfo ZonesField =
            AccessTools.Field(typeof(BotSpawner), "_allBotZones");
        private static readonly FieldInfo SpawnSystemField =
            AccessTools.Field(typeof(BotSpawner), "_spawnSystem");
        private static readonly FieldInfo PlayerCorpseField =
            AccessTools.Field(typeof(Player), "Corpse");

        private readonly HashSet<string> _warmResources =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _cancellation;
        private Coroutine _loop;
        private BotOwner _bot;
        private Corpse _corpse;
        private BotSpawner _capacitySpawner;
        private bool _capacityReserved;
        private string _profileId;
        private string _status = "Disabled";
        private JObject _profileTemplate;
        private GameWorld _assetPoolWorld;
        private LineRenderer _pathLine;
        private LineRenderer _errorLine;
        private float _nextDiagnosticTime;
        private Vector3 _lastDiagnosticPosition;
        private float _lastDiagnosticSampleTime;
        private Vector3[] _lockedRoute;
        private Vector3 _lockedStart;
        private Vector3 _lockedEnd;

        private void Awake()
        {
            Settings.PreviewEnabled.SettingChanged += OnEnabledChanged;
            CreatePathLine();
        }

        private void Start()
        {
            if (Settings.PreviewEnabled.Value) StartPreview();
        }

        private void OnDestroy()
        {
            Settings.PreviewEnabled.SettingChanged -= OnEnabledChanged;
            StopPreview(true);
            if (_pathLine != null) Destroy(_pathLine.gameObject);
            if (_errorLine != null) Destroy(_errorLine.gameObject);
        }

        private void OnEnabledChanged(object sender, EventArgs args)
        {
            if (Settings.PreviewEnabled.Value) StartPreview();
            else StopPreview(true);
        }

        private void StartPreview()
        {
            if (_loop != null) return;
            _lockedRoute = null;
            _cancellation = new CancellationTokenSource();
            _loop = StartCoroutine(PreviewLoop(_cancellation.Token));
        }

        private void StopPreview(bool clean)
        {
            if (_cancellation != null)
            {
                _cancellation.Cancel();
                _cancellation.Dispose();
                _cancellation = null;
            }
            if (_loop != null)
            {
                StopCoroutine(_loop);
                _loop = null;
            }
            if (clean) CleanupOwnedObjects();
            _status = "Disabled";
            ShowPath(null);
            _lockedRoute = null;
        }

        private IEnumerator PreviewLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && Settings.PreviewEnabled.Value)
            {
                GameWorld world = Singleton<GameWorld>.Instantiated
                    ? Singleton<GameWorld>.Instance : null;
                if (world != null && !ReferenceEquals(world, _assetPoolWorld))
                {
                    _warmResources.Clear();
                    _lockedRoute = null;
                    _assetPoolWorld = world;
                }
                Player local = world != null ? world.MainPlayer : null;
                BotsController controller;
                BotSpawner spawner = GetSpawner(out controller);
                if (local == null || spawner == null || controller == null ||
                    IsGameEnding(spawner) || !spawner.IsProfilesLoaded)
                {
                    _status = "Waiting for an active raid and bot spawner";
                    yield return new WaitForSecondsRealtime(1f);
                    continue;
                }

                Vector3 start;
                Vector3 end;
                Vector3[] pathCorners;
                if (_lockedRoute != null)
                {
                    start = _lockedStart;
                    end = _lockedEnd;
                    pathCorners = _lockedRoute;
                }
                else if (!TryBuildRoute(local, out start, out end, out pathCorners))
                {
                    _status = "No complete NavMesh line found near the player";
                    yield return new WaitForSecondsRealtime(1f);
                    continue;
                }
                else
                {
                    _lockedStart = start;
                    _lockedEnd = end;
                    _lockedRoute = (Vector3[])pathCorners.Clone();
                }
                ShowPath(pathCorners);

                Task<BotOwner> spawnTask = SpawnOne(spawner, controller, start, token);
                while (!spawnTask.IsCompleted && !token.IsCancellationRequested)
                    yield return null;
                if (token.IsCancellationRequested) break;
                if (spawnTask.IsFaulted || spawnTask.Result == null)
                {
                    Exception error = spawnTask.Exception != null
                        ? spawnTask.Exception.GetBaseException() : null;
                    _status = "Spawn failed: " + (error != null ? error.Message : "unknown error");
                    Plugin.Log.LogWarning("[RagdollPreview] " + _status);
                    ReleaseCapacity();
                    yield return new WaitForSecondsRealtime(2f);
                    continue;
                }

                _bot = spawnTask.Result;
                _profileId = _bot.Profile.Id;
                Player player = _bot.GetPlayer;
                player.Teleport(start, false);
                float activationTimeout = Time.realtimeSinceStartup + 30f;
                float nextActivationDiagnostic = 0f;
                while (_bot != null && _bot.BotState != EBotState.Active &&
                       _bot.BotState != EBotState.ActiveFail &&
                       Time.realtimeSinceStartup < activationTimeout)
                {
                    if (Settings.PreviewDiagnostics.Value &&
                        Time.realtimeSinceStartup >= nextActivationDiagnostic)
                    {
                        nextActivationDiagnostic = Time.realtimeSinceStartup + 1f;
                        Plugin.Log.LogInfo(string.Format(
                            "[RagdollPreview:Activation] state={0} weaponReady={1} alive={2} remaining={3:0.0}s",
                            _bot.BotState,
                            _bot.WeaponManager != null && _bot.WeaponManager.IsReady,
                            IsAlive(player),
                            activationTimeout - Time.realtimeSinceStartup));
                    }
                    yield return null;
                }
                if (_bot == null || _bot.BotState != EBotState.Active)
                {
                    Plugin.Log.LogWarning(string.Format(
                        "[RagdollPreview] Bot activation failed: state={0}, weaponReady={1}. EFT may not have finished loading bot behaviours.",
                        _bot != null ? _bot.BotState.ToString() : "NULL",
                        _bot != null && _bot.WeaponManager != null &&
                        _bot.WeaponManager.IsReady));
                    if (IsAlive(player))
                        KillPreviewBot(_bot);
                    ReleaseCapacity();
                    yield return new WaitForSecondsRealtime(1f);
                    continue;
                }
                RagdollPreviewBotPatches.Register(_bot);
                _bot.Mover.Stop();
                yield return new WaitForSeconds(0.25f);

                Settings.PreviewMotion mode = Settings.PreviewMode.Value;
                int routeCorner = 1;
                DrivePlayer(_bot, mode, pathCorners, ref routeCorner);
                _nextDiagnosticTime = 0f;
                _lastDiagnosticPosition = player.Position;
                _lastDiagnosticSampleTime = Time.time;
                LogMovementDiagnostics(_bot, mode, pathCorners, 0f, true);
                _status = "Previewing " + mode;
                float moveStart = Time.time;
                float killAt = Mathf.Min(Settings.PreviewKillTime.Value,
                    Settings.PreviewPathTime.Value);
                while (IsAlive(player) && Time.time - moveStart <
                       Settings.PreviewPathTime.Value)
                {
                    DrivePlayer(_bot, mode, pathCorners, ref routeCorner);
                    LogMovementDiagnostics(_bot, mode, pathCorners,
                        Time.time - moveStart, false);
                    if (Time.time - moveStart >= killAt) break;
                    yield return null;
                }

                if (IsAlive(player))
                {
                    KillPreviewBot(_bot);
                }
                _corpse = GetPlayerCorpse(player);
                HideErrorLine();
                RagdollPreviewBotPatches.Unregister(_bot);
                ReleaseCapacity();
                _bot = null;
                _status = "Displaying ragdoll (" +
                    (Settings.Enabled.Value ? "Ragdoll Kinetics" : "EFT default") + ")";

                float findUntil = Time.time + 3f;
                while (_corpse == null && Time.time < findUntil)
                {
                    _corpse = GetPlayerCorpse(player);
                    yield return null;
                }
                float corpseUntil = Time.time + Settings.PreviewCorpseTime.Value;
                while (!token.IsCancellationRequested && Time.time < corpseUntil)
                    yield return null;
                if (_corpse != null)
                    yield return RetireCorpse(_corpse);
                _corpse = null;
                _profileId = null;
                yield return null;
            }
            _loop = null;
        }

        private static void DrivePlayer(BotOwner bot,
            Settings.PreviewMotion mode, Vector3[] route, ref int cornerIndex)
        {
            if (bot == null || bot.Mover == null || bot.IsDead) return;
            Player player = bot.GetPlayer;
            if (player == null || player.MovementContext == null) return;
            bot.Mover.Stop();
            bool moving = mode == Settings.PreviewMotion.Walking ||
                          mode == Settings.PreviewMotion.Running;
            bot.Mover.DoProne(false);
            float targetPose = mode == Settings.PreviewMotion.Crouching ? 0.15f : 1f;
            player.MovementContext.SetPoseLevel(targetPose, force: true);

            if (!moving || route == null || route.Length < 2 ||
                cornerIndex >= route.Length)
            {
                player.EnableSprint(false);
                bot.Mover.Sprint(false);
                player.Move(Vector2.zero);
                return;
            }

            Vector3 position = player.Position;
            Vector3 toCorner = route[cornerIndex] - position;
            toCorner.y = 0f;
            while (toCorner.sqrMagnitude <= 0.16f &&
                   cornerIndex < route.Length - 1)
            {
                cornerIndex++;
                toCorner = route[cornerIndex] - position;
                toCorner.y = 0f;
            }
            if (toCorner.sqrMagnitude <= 0.16f)
            {
                player.EnableSprint(false);
                bot.Mover.Sprint(false);
                player.Move(Vector2.zero);
                return;
            }

            Vector3 direction = toCorner.normalized;
            float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            Vector2 look = new Vector2(yaw, 0f);
            player.MovementContext.SetDirectlyLookRotations(look, look);
            player.MovementContext.ApplyRotation(Quaternion.LookRotation(direction,
                Vector3.up));
            player.CharacterController.SetSteerDirection(direction);
            bool sprint = mode == Settings.PreviewMotion.Running;
            bot.Mover.Sprint(sprint);
            player.EnableSprint(sprint);
            player.MovementContext.SprintSpeed = sprint ? 2f : 1f;
            player.MovementContext.SetCharacterMovementSpeed(
                sprint ? 1f : 0.45f, true);
            player.Move(Vector2.up);
        }

        private static bool TryBuildRoute(Player local, out Vector3 start,
            out Vector3 end, out Vector3[] corners)
        {
            start = end = Vector3.zero;
            corners = null;
            Vector3 forward = Vector3.ProjectOnPlane(local.Transform.forward,
                Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.1f) forward = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3[] directions = { forward, right, -right, -forward };
            for (int i = 0; i < directions.Length; i++)
            {
                Vector3 requestedStart = local.Position + directions[i] * 4f;
                NavMeshHit startHit;
                NavMeshHit endHit;
                if (!NavMesh.SamplePosition(requestedStart, out startHit, 8f,
                        NavMesh.AllAreas) ||
                    !NavMesh.SamplePosition(startHit.position + directions[i] *
                        Settings.PreviewRouteLength.Value, out endHit, 1.5f,
                        NavMesh.AllAreas)) continue;
                if (Vector3.Distance(startHit.position, endHit.position) <
                    Settings.PreviewRouteLength.Value * 0.8f) continue;
                NavMeshPath path = new NavMeshPath();
                if (!NavMesh.CalculatePath(startHit.position, endHit.position,
                        NavMesh.AllAreas, path) ||
                    path.status != NavMeshPathStatus.PathComplete) continue;
                start = startHit.position;
                end = endHit.position;
                corners = path.corners;
                return true;
            }
            return false;
        }

        private void CreatePathLine()
        {
            GameObject holder = new GameObject("RagdollPreviewPath");
            holder.transform.SetParent(transform, false);
            _pathLine = holder.AddComponent<LineRenderer>();
            _pathLine.useWorldSpace = true;
            _pathLine.startWidth = 0.075f;
            _pathLine.endWidth = 0.075f;
            _pathLine.startColor = new Color(0.1f, 1f, 0.2f, 0.95f);
            _pathLine.endColor = new Color(1f, 0.2f, 0.1f, 0.95f);
            _pathLine.material = new Material(Shader.Find("Sprites/Default"));
            _pathLine.positionCount = 0;
            _pathLine.enabled = false;
            _errorLine = CreateLine(holder.transform, "RouteError",
                new Color(1f, 0.05f, 0.05f, 1f), 0.045f);
        }

        private static LineRenderer CreateLine(Transform parent, string name,
            Color color, float width)
        {
            GameObject holder = new GameObject(name);
            holder.transform.SetParent(parent, false);
            LineRenderer line = holder.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.startWidth = width;
            line.endWidth = width;
            line.startColor = color;
            line.endColor = color;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.positionCount = 0;
            line.enabled = false;
            return line;
        }

        private void ShowPath(Vector3[] corners)
        {
            if (_pathLine == null) return;
            bool visible = corners != null && corners.Length >= 2 &&
                           Settings.PreviewEnabled.Value;
            _pathLine.enabled = visible;
            _pathLine.positionCount = visible ? corners.Length : 0;
            if (!visible) return;
            for (int i = 0; i < corners.Length; i++)
                _pathLine.SetPosition(i, corners[i] + Vector3.up * 0.08f);
        }

        private void LogMovementDiagnostics(BotOwner bot,
            Settings.PreviewMotion mode, Vector3[] route, float elapsed,
            bool routeIssued)
        {
            if (!Settings.PreviewDiagnostics.Value || bot == null ||
                bot.GetPlayer == null)
            {
                HideErrorLine();
                return;
            }

            Player player = bot.GetPlayer;
            Vector3 closest = ClosestPointOnRoute(player.Position, route);
            float error = Vector3.Distance(player.Position, closest);
            if (_errorLine != null)
            {
                _errorLine.enabled = true;
                _errorLine.positionCount = 2;
                _errorLine.SetPosition(0, player.Position + Vector3.up * 0.12f);
                _errorLine.SetPosition(1, closest + Vector3.up * 0.12f);
            }

            if (!routeIssued && Time.time < _nextDiagnosticTime) return;
            float sampleDuration = Mathf.Max(0.001f,
                Time.time - _lastDiagnosticSampleTime);
            float measuredSpeed = Vector3.Distance(player.Position,
                _lastDiagnosticPosition) / sampleDuration;
            _lastDiagnosticPosition = player.Position;
            _lastDiagnosticSampleTime = Time.time;
            _nextDiagnosticTime = Time.time + 0.5f;

            BotMover mover = bot.Mover;
            Vector3? target = mover != null ? mover.TargetPoint : null;
            if (routeIssued)
                Plugin.Log.LogInfo("[RagdollPreview:Route] " +
                    string.Join(" -> ", route.Select(point =>
                        point.ToString("F2")).ToArray()));
            Plugin.Log.LogInfo(string.Format(
                "[RagdollPreview:Move] issued={0} t={1:0.00}s mode={2} botState={3} moverState={4} hasPath={5} moving={6} pause={7} pos={8} target={9} remaining={10:0.00} lineError={11:0.00}m speedSetting={12:0.00} measuredSpeed={13:0.00} velocity={14:0.00} sprint={15} pose={16:0.00}/{17:0.00} moveDir={18}",
                routeIssued, elapsed, mode, bot.BotState,
                mover != null ? mover.CurrentState.ToString() : "NULL",
                mover != null && mover.HasPathAndNoComplete,
                mover != null && mover.IsMoving,
                mover != null && mover.Pause,
                player.Position.ToString("F2"),
                target.HasValue ? target.Value.ToString("F2") : "NULL",
                mover != null ? mover.DistDestination : -1f,
                error, player.Speed, measuredSpeed, player.Velocity.magnitude,
                mover != null && mover.Sprinting, player.PoseLevel,
                mover != null ? mover.TargetPose : -1f,
                mover != null ? mover.NormDirCurPoint.ToString("F2") : "NULL"));
        }

        private void HideErrorLine()
        {
            if (_errorLine == null) return;
            _errorLine.enabled = false;
            _errorLine.positionCount = 0;
        }

        private static Vector3 ClosestPointOnRoute(Vector3 position,
            Vector3[] route)
        {
            if (route == null || route.Length == 0) return position;
            Vector3 closest = route[0];
            float best = (position - closest).sqrMagnitude;
            for (int i = 0; i < route.Length - 1; i++)
            {
                Vector3 start = route[i];
                Vector3 delta = route[i + 1] - start;
                float denominator = delta.sqrMagnitude;
                float t = denominator > 0.0001f
                    ? Mathf.Clamp01(Vector3.Dot(position - start, delta) /
                        denominator) : 0f;
                Vector3 candidate = start + delta * t;
                float distance = (position - candidate).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                closest = candidate;
            }
            return closest;
        }

        private async Task<BotOwner> SpawnOne(BotSpawner spawner,
            BotsController controller, Vector3 position, CancellationToken token)
        {
            _status = _profileTemplate == null
                ? "Requesting one reusable scav template"
                : "Cloning cached scav template";
            Profile profile = await RequestProfile(token);
            token.ThrowIfCancellationRequested();
            ResourceKey[] resources = profile.GetAllPrefabPaths(false)
                .Where(x => x != ResourceKey.EmptyResourceKey).Distinct().ToArray();
            ResourceKey[] cold = resources.Where(x =>
                !_warmResources.Contains(ResourceKeyName(x))).ToArray();
            if (cold.Length > 0)
            {
                _status = "Warming " + cold.Length + " bot assets";
                await LoadPools(cold, token);
                foreach (ResourceKey resource in cold)
                    _warmResources.Add(ResourceKeyName(resource));
            }

            BotSpawnParams spawnParams = new BotSpawnParams
            { Id_spawn = "ragdoll-preview-" + Guid.NewGuid().ToString("N") };
            GetProfileDataParams dataParams = new GetProfileDataParams(
                profile.Info.Side, profile.Info.Settings.Role,
                profile.Info.Settings.BotDifficulty, 5f, spawnParams, false);
            BotCreationData creation = BotCreationData.CreateWithoutProfile(dataParams);
            creation.AddProfile(profile);
            BotZone zone;
            ISpawnPoint point;
            if (!TrySelectSpawnPoint(spawner, controller, creation, position,
                    out zone, out point))
                throw new InvalidOperationException("No compatible AI zone/spawn point exists.");
            creation.AddPosition(position, point.CorePointId);

            token.ThrowIfCancellationRequested();
            ReserveCapacity(spawner);
            TaskCompletionSource<BotOwner> source = new TaskCompletionSource<BotOwner>();
            SetInSpawnProcess(spawner, GetInSpawnProcess(spawner) + 1);
            try
            {
                spawner.method_10(zone, creation, bot => source.TrySetResult(bot),
                    spawner.GetCancelToken());
            }
            catch
            {
                SetInSpawnProcess(spawner,
                    Math.Max(0, GetInSpawnProcess(spawner) - 1));
                throw;
            }
            Task completed = await Task.WhenAny(source.Task,
                Task.Delay(30000, token));
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(completed, source.Task))
                throw new TimeoutException("Bot activation timed out.");
            BotOwner result = await source.Task;
            if (token.IsCancellationRequested && result != null &&
                IsAlive(result.GetPlayer))
                KillPreviewBot(result);
            token.ThrowIfCancellationRequested();
            return result;
        }

        private async Task<Profile> RequestProfile(CancellationToken token)
        {
            if (_profileTemplate != null)
                return CreateProfileClone();

            string request = JsonConvert.SerializeObject(new
            {
                conditions = new[] { new { Role = WildSpawnType.assault.ToString(),
                    Limit = 1, Difficulty = BotDifficulty.normal.ToString() } }
            });
            Task<string> requestTask = RequestHandler.PostJsonAsync(
                "/client/game/bot/generate", request);
            Task completed = await Task.WhenAny(requestTask, Task.Delay(30000, token));
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(completed, requestTask))
                throw new TimeoutException("SPT bot profile request timed out.");
            JToken root = JToken.Parse(await requestTask);
            if ((root.Value<int?>("err") ?? 0) != 0)
                throw new InvalidOperationException(root.Value<string>("errmsg") ??
                    "SPT bot generation failed.");
            JToken data = root["data"] ?? root;
            ProfileDescriptor[] descriptors =
                JsonConvert.DeserializeObject<ProfileDescriptor[]>(data.ToString());
            if (descriptors == null || descriptors.Length == 0)
                throw new InvalidOperationException("SPT returned no scav profile.");
            _profileTemplate = JObject.FromObject(descriptors[0]);
            Plugin.Log.LogInfo("[RagdollPreview] Cached one scav loadout and appearance for all preview respawns.");
            return CreateProfileClone();
        }

        private Profile CreateProfileClone()
        {
            JObject clone = (JObject)_profileTemplate.DeepClone();
            Dictionary<string, string> replacements =
                new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (JProperty property in clone.Descendants()
                .OfType<JProperty>().Where(x => x.Name == "_id" &&
                    x.Value.Type == JTokenType.String).ToArray())
            {
                string oldId = property.Value.Value<string>();
                if (string.IsNullOrEmpty(oldId)) continue;
                string newId;
                if (!replacements.TryGetValue(oldId, out newId))
                {
                    newId = new MongoID(true).ToString();
                    replacements.Add(oldId, newId);
                }
                property.Value = newId;
            }

            foreach (JValue value in clone.Descendants().OfType<JValue>()
                .Where(x => x.Type == JTokenType.String).ToArray())
            {
                string replacement;
                string current = value.Value<string>();
                if (current != null && replacements.TryGetValue(current,
                        out replacement))
                    value.Value = replacement;
            }

            ProfileDescriptor descriptor = clone.ToObject<ProfileDescriptor>();
            return new Profile(descriptor);
        }

        private static async Task LoadPools(ICollection<ResourceKey> resources,
            CancellationToken token)
        {
            ObjectsFactory factory = Singleton<ObjectsFactory>.Instance;
            object pools = factory.GetPools(ObjectsFactory.PoolsCategory.Raid);
            const BindingFlags flags = BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo convert = pools.GetType().GetMethod("ConvertResourceInfo",
                flags, null, new[] { typeof(ICollection<ResourceKey>) }, null);
            object converted = convert.Invoke(pools, new object[] { resources });
            System.Collections.IList list = (System.Collections.IList)converted;
            for (int i = 0; i < list.Count; i++)
            {
                object item = list[i];
                item.GetType().GetField("PoolSize", flags).SetValue(item, 1);
                list[i] = item;
            }
            MethodInfo load = typeof(ObjectsFactory).GetMethods(flags)
                .First(m => m.Name == "LoadBundlesAndCreatePools" &&
                    m.ReturnType == typeof(Task) && m.GetParameters().Length == 6 &&
                    m.GetParameters()[0].ParameterType == pools.GetType());
            Task task = (Task)load.Invoke(factory, new object[] { pools, converted,
                ObjectsFactory.AssemblyType.Online, Diz.Jobs.JobYieldPriority.Immediate,
                null, token });
            await task;
        }

        private static bool TrySelectSpawnPoint(BotSpawner spawner,
            BotsController controller, BotCreationData creation, Vector3 requested,
            out BotZone selectedZone, out ISpawnPoint selectedPoint)
        {
            selectedZone = null;
            selectedPoint = null;
            BotZone[] zones = ZonesField.GetValue(spawner) as BotZone[];
            ISpawnSystem system = SpawnSystemField.GetValue(spawner) as ISpawnSystem;
            float best = float.MaxValue;
            if (zones == null || system == null) return false;
            foreach (BotZone zone in zones)
            {
                if (zone == null || zone.SpawnPoints == null ||
                    !creation.CanAtZoneByType(zone, controller.ZonesLeaveController)) continue;
                foreach (ISpawnPoint point in zone.SpawnPoints)
                {
                    if (point == null) continue;
                    float score = (point.Position - requested).sqrMagnitude +
                        (system.IsValidSpawn(point, creation, Time.time) ? 0f : 10000000f);
                    if (score >= best) continue;
                    best = score;
                    selectedZone = zone;
                    selectedPoint = point;
                }
            }
            return selectedZone != null;
        }

        private static BotSpawner GetSpawner(out BotsController controller)
        {
            controller = null;
            try
            {
                IBotGame game = Singleton<IBotGame>.Instance;
                controller = game != null ? game.BotsController : null;
                return controller != null ? controller.GetSpawner() : null;
            }
            catch { return null; }
        }

        private static bool IsGameEnding(BotSpawner spawner) =>
            GameEndField != null && (bool)GameEndField.GetValue(spawner);
        private static int GetInSpawnProcess(BotSpawner spawner) =>
            InSpawnProcessField != null ? (int)InSpawnProcessField.GetValue(spawner) : 0;
        private static void SetInSpawnProcess(BotSpawner spawner, int value) =>
            InSpawnProcessField?.SetValue(spawner, value);
        private static string ResourceKeyName(ResourceKey key) =>
            key.path + "\n" + (key.rcid ?? "");
        private static bool IsAlive(Player player) => player != null &&
            player.ActiveHealthController != null && player.ActiveHealthController.IsAlive;

        private static void KillPreviewBot(BotOwner bot)
        {
            Player player = bot != null ? bot.GetPlayer : null;
            if (!IsAlive(player)) return;

            PreviewDeathMarker marker =
                player.GetComponent<PreviewDeathMarker>();
            if (marker == null)
                marker = player.gameObject.AddComponent<PreviewDeathMarker>();
            marker.Arm();
            player.KillMe(EBodyPartColliderType.HeadCommon, 100000f);
        }

        private void ReserveCapacity(BotSpawner spawner)
        {
            if (_capacityReserved) return;
            spawner.SetMaxBots(spawner.MaxBots + 1);
            _capacitySpawner = spawner;
            _capacityReserved = true;
        }

        private void ReleaseCapacity()
        {
            if (!_capacityReserved) return;
            if (_capacitySpawner != null && !IsGameEnding(_capacitySpawner))
                _capacitySpawner.SetMaxBots(Math.Max(0, _capacitySpawner.MaxBots - 1));
            _capacitySpawner = null;
            _capacityReserved = false;
        }

        private static Corpse FindOwnedCorpse(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return null;
            Corpse[] corpses = FindObjectsOfType<Corpse>();
            return corpses.FirstOrDefault(x => x != null && x.PlayerProfileID == profileId);
        }

        private static Corpse GetPlayerCorpse(Player player)
        {
            return player != null && PlayerCorpseField != null
                ? PlayerCorpseField.GetValue(player) as Corpse : null;
        }

        private static IEnumerator RetireCorpse(Corpse corpse)
        {
            if (corpse == null) yield break;

            corpse.StopAllCoroutines();

            Patches.RagdollSkeleton kinetics =
                corpse.GetComponent<Patches.RagdollSkeleton>();
            if (kinetics != null)
                Destroy(kinetics);
            yield return null;

            CorpseRagdoll ragdoll = corpse.Ragdoll;
            if (ragdoll != null)
            {
                RigidbodySpawner[] bodies = ragdoll._rigidbodySpawners;
                if (bodies != null)
                {
                    foreach (RigidbodySpawner body in bodies)
                    {
                        Rigidbody rigidbody = body != null ? body.Rigidbody : null;
                        if (rigidbody == null) continue;
                        PhysicsExtensions.UpdateController.UnsupportRigidbody(rigidbody);
                        rigidbody.collisionDetectionMode = CollisionDetectionMode.Discrete;
                        rigidbody.isKinematic = true;
                    }
                }
                Rigidbody weapon = ragdoll.WeaponRigidbody;
                if (weapon != null)
                {
                    PhysicsExtensions.UpdateController.UnsupportRigidbody(weapon);
                    weapon.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    weapon.isKinematic = true;
                }
                ragdoll._isPhysicsDone = true;
                ragdoll.ClearWeapon();

                CharacterJointSpawner[] joints = ragdoll._jointSpawners;
                if (joints != null)
                    foreach (CharacterJointSpawner joint in joints)
                        if (joint != null) joint.Remove();
                if (bodies != null)
                    foreach (RigidbodySpawner body in bodies)
                        if (body != null) body.Remove();
            }

            yield return null;
            corpse.Kill();
            yield return null;
        }

        private void CleanupOwnedObjects()
        {
            string retiringProfileId = _profileId;
            try
            {
                Player player = _bot != null ? _bot.GetPlayer : null;
                RagdollPreviewBotPatches.Unregister(_bot);
                if (IsAlive(player))
                    KillPreviewBot(_bot);
                _corpse = _corpse != null ? _corpse : FindOwnedCorpse(_profileId);
                if (_corpse != null)
                    StartCoroutine(RetireCorpse(_corpse));
                else if (!string.IsNullOrEmpty(retiringProfileId))
                    StartCoroutine(RetireCorpseWhenCreated(retiringProfileId));
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning("[RagdollPreview] Cleanup failed: " + exception.Message);
            }
            finally
            {
                _bot = null;
                _corpse = null;
                _profileId = null;
                ReleaseCapacity();
            }
        }

        private static IEnumerator RetireCorpseWhenCreated(string profileId)
        {
            float timeout = Time.realtimeSinceStartup + 3f;
            Corpse corpse = null;
            while (corpse == null && Time.realtimeSinceStartup < timeout)
            {
                corpse = FindOwnedCorpse(profileId);
                if (corpse == null) yield return null;
            }
            if (corpse != null)
                yield return RetireCorpse(corpse);
        }
    }

    internal static class RagdollPreviewBotPatches
    {
        private static readonly HashSet<BotOwner> PreviewBots =
            new HashSet<BotOwner>();

        internal static void Register(BotOwner bot)
        {
            if (bot != null) PreviewBots.Add(bot);
        }

        internal static void Unregister(BotOwner bot)
        {
            if (bot != null) PreviewBots.Remove(bot);
        }

        internal static bool IsPreview(BotOwner bot)
        {
            return bot != null && PreviewBots.Contains(bot);
        }

        internal static bool ShouldRunUpdate(BotOwner bot)
        {
            return bot == null || !PreviewBots.Contains(bot);
        }
    }

    internal sealed class PreviewBotManualUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotOwner), nameof(BotOwner.UpdateManual));

        [PatchPrefix]
        private static bool PatchPrefix(BotOwner __instance) =>
            RagdollPreviewBotPatches.ShouldRunUpdate(__instance);
    }

    internal sealed class PreviewBotFixedUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotOwner), nameof(BotOwner.FixedUpdate));

        [PatchPrefix]
        private static bool PatchPrefix(BotOwner __instance) =>
            RagdollPreviewBotPatches.ShouldRunUpdate(__instance);
    }
}
