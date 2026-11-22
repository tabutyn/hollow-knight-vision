using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightVisionInputReceiver
{
    internal sealed partial class ReceiverBehaviour
    {
        // A hit request can be blocked. Observe the target's actual HP after the
        // game's hit handler and its death notification before crediting the nail.
        private sealed class NailContact
        {
            internal HealthManager Target;
            internal int StartingHP;
            internal bool Died;
            internal void OnDeath() { Died = true; }
        }

        private readonly List<NailContact> nailContacts = new List<NailContact>();
        private int outcomeScene = Int32.MinValue;
        private string outcomeEpoch;
        private int enemyDamage, enemyKills;

        private void SetOutcomeScene(int handle)
        {
            if (outcomeScene == handle) return;
            FinishNailContacts();
            outcomeScene = handle;
            outcomeEpoch = Guid.NewGuid().ToString("D");
            enemyDamage = enemyKills = 0;
        }

        private HitInstance ObserveNailHit(Fsm fsm, HitInstance hit)
        {
            var hero = HeroController.instance;
            if (hit.AttackType != AttackTypes.Nail || hit.DamageDealt <= 0 || hero == null ||
                hit.Source == null || !hit.Source.transform.IsChildOf(hero.transform)) return hit;
            var action = fsm.ActiveState == null ? null :
                fsm.ActiveState.ActiveAction as HutongGames.PlayMaker.Actions.TakeDamage;
            var target = action == null || action.Target == null ? null : action.Target.Value;
            if (target == null) return hit;
            var enemy = target.GetComponentInParent<HealthManager>();
            if (enemy == null || enemy.hp <= 0 || !enemy.gameObject.activeInHierarchy ||
                (enemy.gameObject.layer != LayerMask.NameToLayer("Enemies") &&
                 enemy.GetComponentInChildren<DamageHero>(true) == null)) return hit;
            SetOutcomeScene(enemy.gameObject.scene.handle);
            foreach (var contact in nailContacts)
                if (contact.Target == enemy) return hit;
            var pending = new NailContact { Target = enemy, StartingHP = enemy.hp };
            enemy.OnDeath += pending.OnDeath;
            nailContacts.Add(pending);
            return hit;
        }

        private void FinishNailContacts()
        {
            foreach (var contact in nailContacts) {
                var enemy = contact.Target;
                if (enemy != null) enemy.OnDeath -= contact.OnDeath;
                if (contact.Died) {
                    enemyKills++;
                    enemyDamage += contact.StartingHP;
                } else if (enemy != null) {
                    enemyDamage += Math.Max(0, contact.StartingHP - Math.Max(0, enemy.hp));
                }
            }
            nailContacts.Clear();
        }
    }
}
