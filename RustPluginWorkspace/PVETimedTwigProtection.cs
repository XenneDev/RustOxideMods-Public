/*

#############################################
PVE Temporary Twig Protection by Xenne
v1.0.5
#############################################

This plugin makes twig structures destroyable after 30 minutes (set TWIG_LIFTETIME to adjust).
When using a PVE mod like TruePVE or NextGenPVE make sure that twig is destroyable at all times. This plugin
handles the protection and will not interfere again after the protection time has been reached. 

You are free to adjust and modify to your liking. 

*/


using System.Collections.Generic;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("TwigController", "Xenne", "1.0.5")]
    public class TwigController : RustPlugin
    {
        private const float TWIG_LIFETIME = 1800f;
        private const float SAVE_INTERVAL = 60f;
        private Dictionary<NetworkableId, float> protectedTwigs = new Dictionary<NetworkableId, float>();

        private void Init()
        {
            protectedTwigs.Clear();
            SaveData();
            LoadData();
            timer.Every(SAVE_INTERVAL, () => SaveData());
        }

        // When an Entity has been placed
        void OnEntityBuilt(Planner plan, GameObject go)
        {
            // Get the BuildingBlock component
            var buildingBlock = go.GetComponent<BuildingBlock>();

            // Check if the grade is Twigs
            if (buildingBlock && buildingBlock.grade == BuildingGrade.Enum.Twigs)
            {
                // Add the networkId to the dictionary
                protectedTwigs.Add(buildingBlock.net.ID, Time.realtimeSinceStartup);
            }
        }


        // When an Entity takes damange
        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (entity is BuildingBlock && ((BuildingBlock)entity).grade == BuildingGrade.Enum.Twigs)
            {
                if (protectedTwigs.ContainsKey(entity.net.ID))
                {
                    var timePlaced = protectedTwigs[entity.net.ID];
                    var elapsedTime = Time.realtimeSinceStartup - timePlaced;
                    var remainingTime = TWIG_LIFETIME - elapsedTime;

                    if (remainingTime > 0)
                    {
                        SendProtectionMessage(hitInfo.InitiatorPlayer, remainingTime);
                        return true; // Dit voorkomt schade
                    }
                    else
                    {
                        protectedTwigs.Remove(entity.net.ID);  // Omdat de beschermingstijd voorbij is, verwijderen we het uit de dictionary
                        return null;
                    }
                }
                else
                {
                    return null; // Laat de schade doorgaan na 30 minuten
                }

            }

            return null;
        }

        // Send message to the attacker that the twig is protected for x minutes & seconds
        void SendProtectionMessage(BasePlayer player, float remainingTime)
        {
            if (player != null)
            {
                int minutes = Mathf.FloorToInt(remainingTime / 60);
                int seconds = Mathf.FloorToInt(remainingTime - minutes * 60);
                player.ChatMessage($"<color=red>[Timed Twig Protection]</color>\nThis twig is protected for {minutes} minutes and {seconds} seconds");
            }
        }





        void Unload()
        {
            protectedTwigs.Clear();
            SaveData();
        }

        private void SaveData()
        {
            // Ga door de dictionary en verwijder twigs die de tijdslimiet hebben overschreden
            List<NetworkableId> twigsToRemove = new List<NetworkableId>();
            foreach (var twig in protectedTwigs)
            {
                float elapsedTime = Time.realtimeSinceStartup - twig.Value;
                if (elapsedTime > TWIG_LIFETIME)
                {
                    twigsToRemove.Add(twig.Key);
                }
            }
            foreach (var twigId in twigsToRemove)
            {
                protectedTwigs.Remove(twigId);
            }

            // Sla de aangepaste dictionary op
            Interface.Oxide.DataFileSystem.WriteObject("TwigControllerData", protectedTwigs);
        }

        private void LoadData()
        {
            var loadedData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<NetworkableId, float>>("TwigControllerData");
            if (loadedData != null)
            {
                protectedTwigs = loadedData;
            }
        }
    }
}