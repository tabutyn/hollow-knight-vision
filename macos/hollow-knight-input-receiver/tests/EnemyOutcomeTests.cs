using System;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using static HollowKnightVisionInputReceiver.ProtocolTests;

namespace HollowKnightVisionInputReceiver
{
    // This partial adds fixture access to the real receiver hit handler in the
    // test assembly. Unity's hit application is represented by HP changes.
    internal sealed partial class ReceiverBehaviour
    {
        internal static void TestEnemyOutcomes()
        {
            const int room = 19;
            var receiver = new ReceiverBehaviour();
            receiver.SetOutcomeScene(room);
            var epoch = receiver.outcomeEpoch;
            Assert("combat outcome has a valid room epoch", Guid.TryParse(epoch, out _));

            var enemy = Enemy(room, LayerMask.EnemyLayer, 10);
            var target = new FsmGameObject { Value = enemy.gameObject };
            var action = new TakeDamage { Target = target };
            var state = new FsmState { ActiveAction = action };
            var fsm = new Fsm { ActiveState = state };
            var source = new GameObject("nail");
            var hit = new HitInstance { AttackType = AttackTypes.Nail, DamageDealt = 5, Source = source };
            var priorHero = HeroController.instance;
            try {
                HeroController.instance = null;
                Reject("no Knight");
                HeroController.instance = new HeroController();
                Reject("unrelated source");
                source.transform.parent = HeroController.instance.transform;
                hit.Source = null; Reject("absent source"); hit.Source = source;
                hit.AttackType = AttackTypes.Spell; Reject("spell"); hit.AttackType = AttackTypes.Nail;
                hit.DamageDealt = 0; Reject("zero damage"); hit.DamageDealt = 5;
                fsm.ActiveState = null; Reject("inactive FSM"); fsm.ActiveState = state;
                state.ActiveAction = new object(); Reject("unrelated FSM action"); state.ActiveAction = action;
                action.Target = null; Reject("missing FSM target"); action.Target = target;
                target.Value = null; Reject("missing hit target"); target.Value = enemy.gameObject;
                enemy.gameObject.ParentHealth = null; Reject("no health component"); enemy.gameObject.ParentHealth = enemy;
                enemy.hp = 0; Reject("already dead"); enemy.hp = 10;
                enemy.gameObject.activeInHierarchy = false; Reject("inactive target"); enemy.gameObject.activeInHierarchy = true;
                enemy.gameObject.layer = 0; Reject("harmless target"); enemy.gameObject.layer = LayerMask.EnemyLayer;

                Observe();
                receiver.FinishNailContacts();
                Assert("blocked nail adds no damage, kill, or lingering death callback",
                    receiver.enemyDamage == 0 && receiver.enemyKills == 0 && enemy.DeathListeners == 0);

                Observe();
                enemy.hp = 12;
                receiver.FinishNailContacts();
                Assert("healing cannot subtract damage", receiver.enemyDamage == 0);

                Observe();
                enemy.hp = 8;
                Observe();
                enemy.hp = 5;
                Assert("multiple hits in one frame share one starting HP and one death callback",
                    receiver.nailContacts.Count == 1 && enemy.DeathListeners == 1);
                receiver.FinishNailContacts();
                Assert("combat telemetry credits actual HP lost under the same room epoch",
                    receiver.outcomeEpoch == epoch && receiver.enemyDamage == 7 && receiver.enemyKills == 0);

                // A contact-damage component identifies enemies outside the Enemies layer.
                var other = Enemy(room, 0, 3);
                other.ChildDamage = new DamageHero();
                target.Value = other.gameObject;
                Observe();
                other.Die();
                receiver.FinishNailContacts();
                receiver.FinishNailContacts();
                var telemetry = new HackerActorTelemetry {
                    OutcomeEpoch = receiver.outcomeEpoch, EnemyDamage = receiver.enemyDamage, EnemyKills = receiver.enemyKills
                }.ToJson();
                var outcomes = (JObject)telemetry["enemyOutcomes"];
                Assert("lethal strike credits remaining HP once and emits one kill",
                    outcomes.Value<string>("epoch") == epoch && outcomes.Value<int>("damage") == 10
                    && outcomes.Value<int>("kills") == 1 && other.DeathListeners == 0);

                target.Value = enemy.gameObject;
                Observe();
                enemy.hp = 3;
                receiver.SetOutcomeScene(room + 1);
                receiver.FinishNailContacts();
                Assert("room transition detaches earlier contacts and starts new outcome counters",
                    receiver.outcomeEpoch != epoch && receiver.enemyDamage == 0 && receiver.enemyKills == 0
                    && enemy.DeathListeners == 0 && receiver.nailContacts.Count == 0);
            }
            finally { HeroController.instance = priorHero; }
            Console.WriteLine("PASS receiver credits actual nail damage and kills within the current room epoch");

            void Observe()
            {
                Assert("observing a hit preserves the game's original damage and source",
                    receiver.ObserveNailHit(fsm, hit).Equals(hit));
            }

            void Reject(string reason)
            {
                Observe();
                Assert("hit excluded from combat outcomes for " + reason,
                    receiver.nailContacts.Count == 0 && enemy.DeathListeners == 0);
            }
        }

        private static HealthManager Enemy(int room, int layer, int hp)
        {
            var body = new GameObject("enemy") { scene = new Scene { handle = room }, layer = layer };
            var enemy = new HealthManager { hp = hp, gameObject = body };
            body.ParentHealth = enemy;
            return enemy;
        }
    }
}
