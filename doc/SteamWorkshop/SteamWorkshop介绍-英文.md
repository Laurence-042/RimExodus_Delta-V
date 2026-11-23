[h1]RimExodus: Delta-V[/h1]

[b]⚠ This mod is currently in alpha. Its save-data structure is mostly stable and its core mechanics are relatively complete, but mechanics and numerical values are still changing frequently, and there is still not enough content to support a complete gameplay loop. Expect plenty of bugs and compatibility issues. The main purpose of this release is to let interested players try the mod early and offer suggestions on balance and future content. Although the mod can technically be added mid-save, I have not specifically implemented uninstall support or tested this scenario thoroughly, so I still do not recommend using it in a serious playthrough.[/b]

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus_Delta-V/master/doc/promo/name.jpg[/img]

Have you ever imagined that perhaps ships in RimWorld - Odyssey would not have to hop around like grasshoppers?

Have you ever imagined that structures in RimWorld could have continuous linear and angular velocity?

Have you ever imagined that a RimWorld map might no longer be a finite cage, but an ark that shelters you in space?

Well, here it is!

Delta-V adds continuous flight and ship combat to RimWorld - Odyssey.

You can take your own gravship out of port, fly alongside other ships in orbit, dock, open fire, or even ram them.

Shells, lasers, and missiles cross the void between ships, while a boarding shuttle packed with marines waits in the corner, ready to hit the afterburners and crash into the enemy ship.

You can even cut a section off an enemy station and tow it away with a tractor beam!

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus_Delta-V/master/doc/promo/demo.webp[/img]

[h1]What does it do?[/h1]

Put simply, it treats a ship map as a “SpaceObject”, a container that can hold a set of structures moving together. RimExodus Framework, which matured during the development of RimExodus, renders multiple SpaceObjects in the same view so that ships are no longer confined to the map grid.

[b]Space is limited here, so please see “开局、靠港与星区设置 / Getting Started, Docking, and Sector Settings” in Discussions for details. I strongly recommend reading it before playing. Some features can be hard to understand if you skip the starting tutorial.[/b]

[list]
[*][b]Ships without grav engines[/b]: A pilot console and connected gravship substructure are enough to define a ship. Add thrusters and it can fly freely in space. Be warned that the small vanilla Odyssey ship with only two rear-facing thrusters will handle terribly here. Ship mass, center of mass, thruster placement, and cargo carried in Delta-V containers all affect handling.

[*][b]Continuous ship combat[/b]: You now need to account for relative velocity, seek favorable firing positions, and attack weak points without exposing your own. You have a wide selection of weapons with distinct characteristics and three shield types with different mechanics. Some weapons can also take attachments that substantially change their performance and role. Every weapon has strengths and weaknesses, and impact angle matters. Even heavy armor-piercing rounds can ricochet if they strike too obliquely. See “舰炮、护盾与工程设备 / Ship Weapons, Shields, and Engineering Equipment” for details.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus_Delta-V/master/doc/promo/battle.jpg[/img]

[b]Ship collisions[/b]
[img]https://raw.githubusercontent.com/Laurence-042/RimExodus_Delta-V/master/doc/promo/collision.jpg[/img]

[*][b]Docking, salvage, and boarding[/b]: Dock with a station, a friendly ship, or a disabled enemy ship to transfer people and cargo. After docking, you can even merge the two ship maps and walk freely between them. Or launch a boarding shuttle from your moon pool and send your marines on a bold boarding action. The marines insist that ramming counts as docking.

[b]Docking at a station[/b]
[img]https://raw.githubusercontent.com/Laurence-042/RimExodus_Delta-V/master/doc/promo/docking.jpg[/img]

[b]Boarding combat[/b]
[img]https://raw.githubusercontent.com/Laurence-042/RimExodus_Delta-V/master/doc/promo/bording.jpg[/img]

[*][b]Trade and contracts[/b]: Some stations have trade terminals where you can trade and accept contracts. If you think the prices are too high... you can always stage a heroic boarding action, hack the station's cargo section, and shop for free. You can even cut the whole cargo section loose and take it with you.

[b]Cutting and towing station sections[/b]
[img]https://raw.githubusercontent.com/Laurence-042/RimExodus_Delta-V/master/doc/promo/drag.jpg[/img]

[*][b]Customization[/b]: You can customize which ship types spawn in a sector and how likely each one is to appear. You can even make ships you built yourself spawn in sectors.
[/list]

[b]EVA salvage[/b]
[img]https://raw.githubusercontent.com/Laurence-042/RimExodus_Delta-V/master/doc/promo/eva.jpg[/img]

[b]Shipboard mining[/b]
[img]https://raw.githubusercontent.com/Laurence-042/RimExodus_Delta-V/master/doc/promo/mining.jpg[/img]

[h1]Mod compatibility[/h1]

In principle, the basic features should work with mods that do not alter space mechanics or store their own state on maps. Delta-V implements its own space-combat system in a way similar to Combat Extended, so ordinary turrets cannot serve as Delta-V space weapons without dedicated support. Automatic adaptation is planned, but even when implemented it will only provide flavor-level support. It cannot guarantee reliable balance or automatically reproduce unique weapon mechanics.

See “兼容性与已知限制 / Compatibility and Known Limitations” in Discussions for details.

[h1]Known limitations[/h1]

See “兼容性与已知限制 / Compatibility and Known Limitations” in Discussions for details.

[h1]Performance[/h1]

As long as ships are not fighting, Delta-V adds little continuous overhead. Most calculations are event-driven, there is very little polling, and frequently triggered paths have received dedicated optimization.

During ship combat... all I can say is that I tried. As of 2026-11-20, two reasonably armed sample ships of about 2,000 tiles each, fighting entirely under shipboard AI control, ran at around 120 TPS on a five-year-old first-generation OMEN 16 laptop.

See “性能说明 / Performance Notes” in Discussions for details.

[h1]Settings[/h1]

Every setting has a tooltip. Hover over it for an explanation.

[h1]License and credits[/h1]

The TopologyLab incremental connectivity algorithm used to detect ship breakups was provided by Langtaosha (浪淘沙) from the New Frontiers (《新边疆》) development team. Many thanks for this excellent algorithm.

The trailer features "Rise" by Peppsen (P-Music), sourced from [url=https://steamcommunity.com/sharedfiles/filedetails/?id=725130005]P-Music[/url] and used under [url=https://creativecommons.org/licenses/by/4.0/]CC BY 4.0[/url]. The music track is used unchanged, synchronized with gameplay footage and mixed with game sound effects. The music remains under its original license and is not covered by this project's MIT License.

This project is licensed under the MIT License. [url=https://github.com/Laurence-042/RimExodus_Delta-V]Source code and documentation[/url]

You can report problems in the comments. If possible, please include your log and the actions you took before the problem occurred. If you open a GitHub issue, leaving a note in the comments also helps, since I sometimes miss issues there...
