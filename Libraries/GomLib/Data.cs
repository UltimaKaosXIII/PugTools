using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using GomLib.Models;
using GomLib.Tables;

namespace GomLib
{
    /// <summary>
    /// GomLib.Tables.ArmorPerLevel.TableData[WeaponSpec][Models.ItemQuality][ItemLevel][Stat]<br/>
    /// Possible Stats: MaxWeaponDamage, MinWeaponDamage, ForcePowerRating, TechPowerRating
    /// </summary>
    public class Data : IDisposable
    {
        public WeaponPerLevel weaponPerLevel;
        public AbilityDamageTable abilityDamageTable;
        public ArmorPerLevel armorPerLevel;
        public ItemBudget itemBudget;
        public ItemModifierPackageTablePrototype itemModifierPackageTablePrototype;
        public ItemRating itemRating;
        public ModificationNames modificationNames;
        public SchematicVariationsPrototype schematicVariationsPrototype;
        public ShieldPerLevel shieldPerLevel;
        public QuestDifficulty questDifficulty;
        public HuntingRadius huntingRadius;

        [Newtonsoft.Json.JsonIgnore]
        private DataObjectModel _dom;

        private bool disposed = false;

        public Data(DataObjectModel dom, bool tolerateLegacySchemas = false)
        {
            _dom = dom;

            if (!tolerateLegacySchemas)
            {
                weaponPerLevel = new WeaponPerLevel(_dom);
                abilityDamageTable = new AbilityDamageTable(_dom);
                armorPerLevel = new ArmorPerLevel(_dom);
                itemBudget = new ItemBudget(_dom);
                itemModifierPackageTablePrototype = new ItemModifierPackageTablePrototype(_dom);
                itemRating = new ItemRating(_dom);
                modificationNames = new ModificationNames(_dom);
                schematicVariationsPrototype = new SchematicVariationsPrototype(_dom);
                shieldPerLevel = new ShieldPerLevel(_dom);
                questDifficulty = new QuestDifficulty(_dom);
                huntingRadius = new HuntingRadius(_dom);
                return;
            }

            // Beta/DBLB-v1 data predates several live table layouts. These are convenience helpers, not
            // prerequisites for the DOM itself. Load each independently so one old string/map representation
            // (for example cbtArmorPerLevel) cannot prevent the browsers from opening the legacy client.
            TryLegacyTable("WeaponPerLevel", () => weaponPerLevel = new WeaponPerLevel(_dom));
            TryLegacyTable("AbilityDamageTable", () => abilityDamageTable = new AbilityDamageTable(_dom));
            TryLegacyTable("ArmorPerLevel", () => armorPerLevel = new ArmorPerLevel(_dom));
            TryLegacyTable("ItemBudget", () => itemBudget = new ItemBudget(_dom));
            TryLegacyTable("ItemModifierPackageTablePrototype", () => itemModifierPackageTablePrototype = new ItemModifierPackageTablePrototype(_dom));
            TryLegacyTable("ItemRating", () => itemRating = new ItemRating(_dom));
            TryLegacyTable("ModificationNames", () => modificationNames = new ModificationNames(_dom));
            TryLegacyTable("SchematicVariationsPrototype", () => schematicVariationsPrototype = new SchematicVariationsPrototype(_dom));
            TryLegacyTable("ShieldPerLevel", () => shieldPerLevel = new ShieldPerLevel(_dom));
            TryLegacyTable("QuestDifficulty", () => questDifficulty = new QuestDifficulty(_dom));
            TryLegacyTable("HuntingRadius", () => huntingRadius = new HuntingRadius(_dom));
        }

        private static void TryLegacyTable(string name, Action loader)
        {
            try
            {
                loader();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Legacy GOM helper '" + name + "' is unavailable: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;
            if (disposing)
            {
                if(weaponPerLevel != null) weaponPerLevel.Dispose();
                if (abilityDamageTable != null) abilityDamageTable.Dispose();
                if (armorPerLevel != null) armorPerLevel.Dispose();
                if (itemBudget != null) itemBudget.Dispose();
                if (itemModifierPackageTablePrototype != null) itemModifierPackageTablePrototype.Dispose();
                //itemRating.Dispose();
                //modificationNames.Dispose();
                if (schematicVariationsPrototype != null) schematicVariationsPrototype.Dispose();
                if (shieldPerLevel != null) shieldPerLevel.Dispose();
                if (questDifficulty != null) questDifficulty.Dispose();
                if (huntingRadius != null) huntingRadius.Dispose();
            }
            disposed = true;
        }

        ~Data()
        {
            Dispose(false);
        }

        public void Flush()
        {
            _dom = null;
            weaponPerLevel = null;
            abilityDamageTable = null;
            armorPerLevel = null;
            itemBudget = null;
            itemModifierPackageTablePrototype = null;
            itemRating = null;
            modificationNames = null;
            schematicVariationsPrototype = null;
            shieldPerLevel = null;
            huntingRadius = null;
        }
    }
}
