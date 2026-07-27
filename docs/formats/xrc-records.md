# XRC — per-record payload reference

Companion to [`xrc.md`](xrc.md), which describes the outer record shape shared by every record in an XRC tree. This document lists what the `Data` payload looks like for each `TypeId`.

Every entry below documents:

- The **wire layout** of the `Data` blob in reader order — one field per line, C-style types.
- Any **`SubType` variants** that add or replace fields.
- The **enum values** the record's `uint32`s map onto (`ScriptType`, `SoundType`, `Activity`, `CommandOp`, …).

Payload layouts are taken from ScummVM's Stark engine (`engines/stark/resources/*.cpp`, each class's `readData`) as implemented in [`XrcDisplayDump.DecodePayload`](../../src/TLJExplorer.Core/Formats/XrcDisplayDump.cs). Where a payload has variable-length fields conditional on a preceding count, the fields are shown indented.

## Primitive conventions inside `Data`

Same as documented in [xrc.md](xrc.md):

| Type in Stark | Wire format |
| --- | --- |
| `Bool` | `UInt32` — nonzero means true |
| `Point` | Two `Int32`s: `x, y` |
| `Rect` | Four `Int32`s: `left, top, right, bottom` |
| `Vector3` | Three `Single`s: `x, y, z` |
| `String16` | `UInt16 Length` followed by that many Latin-1 bytes |
| `ResourceReference` | `UInt32 Count` followed by `Count` × `(Byte Type, UInt16 Index)` |

## Field lists by TypeId

### `0x04` Layer

Common prefix, then a `SubType`-specific body.

```text
Single ScrollScale

if SubType == 1: Layer2D
    UInt32 ItemsCount
    repeat ItemsCount times:
        UInt32 ItemIndex
    Bool   Enabled

if SubType == 2: Layer3D
    Bool   ShouldRenderShadows
    Single NearClipPlane
    Single FarClipPlane
    UInt32 MaxShadowLength           -- optional; only present if any bytes remain
```

### `0x05` Camera

```text
Vector3 Position
Vector3 LookDirection
Single  f1                            -- unknown; not used by renderer
Single  FOV
Rect    ViewSize
Vector3 v4                            -- unknown; not used by renderer
```

### `0x06` Floor

```text
UInt32  FacesCount
UInt32  VertexCount
repeat VertexCount times:
    Vector3 v
```

### `0x07` FloorFace

```text
Int16 Index0
Int16 Index1
Int16 Index2
Single DistanceFromCamera
Int16 _                               -- discarded
Int16 _                               -- discarded
Int16 _                               -- discarded
Single Unk2
```

### `0x08` Item

All Item subtypes share `Item::readData` at the front (`enabled + characterIndex`); the visual subclasses then add `ItemVisual::readData` (`clickable`); then per-subtype fields.

```text
Bool   Enabled
Int32  CharacterIndex
```

Then per `SubType`:

```text
SubType == 1: GlobalItemTemplate       -- ItemTemplate; no clickable
    (no further fields)

SubType == 2: InventoryItem            -- ItemVisual only
    Bool Clickable

SubType == 3: LevelItemTemplate        -- ItemTemplate + reference
    ResourceReference Reference

SubType == 5: FloorPositionedImageItem — static prop
SubType == 6: FloorPositionedImageItem — animated prop
    Bool  Clickable
    Int32 FloorFaceIndex
    Point Position

SubType == 7: ImageItem — background element
SubType == 8: ImageItem — background
    Bool               Clickable
    Point              Position
    ResourceReference  Reference

SubType == 10: ModelItem               -- FloorPositionedItem + reference
    Bool               Clickable
    ResourceReference  Reference
```

### `0x09` Script

```text
UInt32 Type                            -- 0=OnGameEvent/enabled, 1=PassiveDialog, 2=OnPlayerAction, 3/4=unknown
UInt32 RunEvent
UInt32 MinChapter
UInt32 MaxChapter
Bool   ShouldResetGameSpeed
```

### `0x0A` AnimHierarchy

```text
UInt32 AnimationRefCount
repeat AnimationRefCount times:
    ResourceReference AnimationRef
ResourceReference ParentAnimHierarchy
Single field_5C
```

### `0x0B` Anim

Common prefix, then `SubType`-specific body.

```text
UInt32 Activity                        -- 0=unspecified, 1=idle, 2=walk, 3=talk, 6=run, 10=idle-action
UInt32 NumFrames

if SubType == 1: AnimImages
    Single field_3C

if SubType == 2: AnimProp
    String16 field_3C                  -- string; purpose unknown
    UInt32   MeshCount
    repeat MeshCount times:
        String16 MeshFile
    String16 TextureFile
    UInt32   MovementSpeed

if SubType == 3: AnimVideo
    String16 SmackerFile
    UInt32   Width
    UInt32   Height
    UInt32   FrameCount
    repeat FrameCount times:
        Point Position
        Rect  Bounds
    Bool   Loop
    UInt32 FrameRateOverride
    Bool   Preload                     -- optional; only present if any bytes remain

if SubType == 4: AnimSkeleton
    String16 AnimationFile
    String16 _                          -- three trailing strings; ScummVM discards
    String16 _
    String16 _
    Bool     Loop
    UInt32   MovementSpeed
    Bool     CastsShadow               -- optional
    UInt32   IdleActionFrequency       -- optional
```

### `0x0C` Direction

```text
UInt32 field_34
UInt32 field_38
UInt32 field_3C
```

Purpose of the three `uint32`s is not derived.

### `0x0D` Image

Common prefix, hit polygons, then `SubType`-specific tail.

```text
String16 Filename
Point    Hotspot
Bool     Transparent
UInt32   TransparentColor              -- packed RGBA; meaningful when Transparent

UInt32   HitPolygonCount
repeat HitPolygonCount times:
    UInt32 PointCount
    repeat PointCount times:
        Point p

if SubType == 2 or SubType == 3: ImageStill
    UInt32 field_44                    -- displayed as field_44 / 33; original unit unknown
    UInt32 field_48

if SubType == 4: ImageText
    Point    Size
    String16 Text
    Byte     R
    Byte     G
    Byte     B
    Byte     _                          -- padding; discarded
    UInt32   Font
```

### `0x0F` AnimScriptItem

A single opcode packed as `(opcode, duration, operand)`. `operand` is `UInt32` for most opcodes but is split into two `Int16`s for opcode 3.

```text
Int32 Opcode
Int32 Duration

if Opcode == 0: DisplayFrame
    Int32 Frame

if Opcode == 1: PlayAnimSound
    Int32 Operand

if Opcode == 2: GoToItem
    Int32 Line

if Opcode == 3: DisplayRandomFrame     -- operand is (maxFrame:int16, minFrame:int16)
    Int16 MaxFrame
    Int16 MinFrame

if Opcode == 4: SleepRandomDuration
    Int32 MaxWait

if Opcode == 5: PlayStockSound
    Int32 StockId

otherwise:
    UInt32 Operand                     -- raw
```

### `0x10` Sound

```text
String16 Filename
UInt32   Enabled                       -- semantics of nonzero values are not fully derived
Bool     Looping
UInt32   field_64
Bool     LoopIndefinitely
UInt32   MaxDuration
Bool     LoadFromFile
UInt32   StockSoundType                -- 3=Background, 5=Stock, other=unknown
String16 SoundName
UInt32   field_6C
UInt32   SoundType                     -- 0=Voice, 1=Effect, 2=Music
Single   Pan
Single   Volume
```

### `0x11` Path

```text
UInt32 field_30

if SubType == 1: Path2D
    UInt32 VertexCount
    repeat VertexCount times:
        Single Weight
        Point  Position
    UInt32 _                            -- trailing count; discarded

if SubType == 2: Path3D
    UInt32 VertexCount
    repeat VertexCount times:
        Single  Weight
        Vector3 Position
    Single SortKey
```

### `0x12` FloorField

```text
UInt32 FacesInFloorField
byte[FacesInFloorField]                -- opaque bitmap; one byte per face flagged
```

### `0x13` Bookmark

```text
Single X
Single Y
```

### `0x15` Knowledge

`SubType` selects the payload's value type.

```text
if SubType == 0: kBoolean
if SubType == 5: kBooleanWithChild
    Bool Value

if SubType == 2: kInteger
if SubType == 3: kInteger2
    Int32 Value

if SubType == 4: kReference
    ResourceReference Value
```

Other subtypes are opaque.

### `0x16` Command

A `Command` record's opcode is stored in its `SubType` field (`CommandOp` list below). The payload is a length-prefixed argument list; each argument declares its own type.

```text
UInt32 ArgumentCount
repeat ArgumentCount times:
    UInt32 ArgType
    if ArgType == 0: (shortcut for "int1 = 0"; no further bytes)
    if ArgType == 1: Int32               -- kTypeInteger1
    if ArgType == 2: Int32               -- kTypeInteger2
    if ArgType == 3: ResourceReference   -- kTypeResourceReference
    if ArgType == 4: String16            -- kTypeString
```

**Command opcodes** (SubType field on the record):

<table>
<thead><tr><th>Op</th><th>Name</th><th>Op</th><th>Name</th><th>Op</th><th>Name</th></tr></thead>
<tbody>
<tr><td>0</td><td>kCommandBegin</td><td>1</td><td>kCommandEnd</td><td>2</td><td>kScriptCall</td></tr>
<tr><td>3</td><td>kDialogCall</td><td>4</td><td>kSetInteractiveMode</td><td>5</td><td>kLocationGoTo</td></tr>
<tr><td>7</td><td>kWalkTo</td><td>8</td><td>kGameLoop</td><td>9</td><td>kScriptPause</td></tr>
<tr><td>10</td><td>kScriptPauseRandom</td><td>11</td><td>kScriptPauseSkippable</td><td>13</td><td>kScriptAbort</td></tr>
<tr><td>19</td><td>kRumbleScene</td><td>20</td><td>kFadeScene</td><td>21</td><td>kSwayScene</td></tr>
<tr><td>22</td><td>kLocationGoToNewCD</td><td>23</td><td>kGameEnd</td><td>24</td><td>kInventoryOpen</td></tr>
<tr><td>25</td><td>kFloatScene</td><td>26</td><td>kBookOfSecretsOpen</td><td>80</td><td>kDoNothing</td></tr>
<tr><td>82</td><td>kItem3DWalkTo</td><td>84</td><td>kItemLookAt</td><td>87</td><td>kItemEnable</td></tr>
<tr><td>88</td><td>kItemSetActivity</td><td>89</td><td>kItemSelectInInventory</td><td>92</td><td>kUseAnimHierarchy</td></tr>
<tr><td>93</td><td>kPlayAnimation</td><td>94</td><td>kScriptEnable</td><td>95</td><td>kShowPlay</td></tr>
<tr><td>96</td><td>kKnowledgeSetBoolean</td><td>100</td><td>kKnowledgeSetInteger</td><td>101</td><td>kKnowledgeAddInteger</td></tr>
<tr><td>103</td><td>kEnableFloorField</td><td>104</td><td>kPlayAnimScriptItem</td><td>105</td><td>kItemAnimFollowPath</td></tr>
<tr><td>107</td><td>kKnowledgeAssignBool</td><td>110</td><td>kKnowledgeAssignInteger</td><td>111</td><td>kLocationScrollTo</td></tr>
<tr><td>112</td><td>kSoundPlay</td><td>115</td><td>kKnowledgeSetIntRandom</td><td>117</td><td>kKnowledgeSubValue</td></tr>
<tr><td>118</td><td>kItemLookDirection</td><td>119</td><td>kStopPlayingSound</td><td>120</td><td>kLayerGoTo</td></tr>
<tr><td>121</td><td>kLayerEnable</td><td>122</td><td>kLocationScrollSet</td><td>123</td><td>kFullMotionVideoPlay</td></tr>
<tr><td>125</td><td>kAnimSetFrame</td><td>126</td><td>kKnowledgeAssignNegatedBool</td><td>127</td><td>kDiaryEnableEntry</td></tr>
<tr><td>128</td><td>kPATChangeTooltip</td><td>129</td><td>kSoundChange</td><td>130</td><td>kLightSetColor</td></tr>
<tr><td>131</td><td>kLightFollowPath</td><td>133</td><td>kItemPlaceDirection</td><td>134</td><td>kItemRotateDirection</td></tr>
<tr><td>135</td><td>kActivateTexture</td><td>136</td><td>kActivateMesh</td><td>137</td><td>kItem3DSetWalkTarget</td></tr>
<tr><td>139</td><td>kSpeakWithoutTalking</td><td>162</td><td>kIsOnFloorField</td><td>163</td><td>kIsItemEnabled</td></tr>
<tr><td>165</td><td>kIsScriptEnabled</td><td>166</td><td>kIsKnowledgeBooleanSet</td><td>170</td><td>kIsKnowledgeIntegerInRange</td></tr>
<tr><td>171</td><td>kIsKnowledgeIntegerAbove</td><td>172</td><td>kIsKnowledgeIntegerEqual</td><td>173</td><td>kIsKnowledgeIntegerLower</td></tr>
<tr><td>174</td><td>kIsScriptActive</td><td>175</td><td>kIsRandom</td><td>176</td><td>kIsAnimScriptItemReached</td></tr>
<tr><td>177</td><td>kIsItemOnPlace</td><td>179</td><td>kIsAnimPlaying</td><td>180</td><td>kIsItemActivity</td></tr>
<tr><td>183</td><td>kIsItemNearPlace</td><td>185</td><td>kIsAnimAtTime</td><td>187</td><td>kIsInventoryOpen</td></tr>
</tbody>
</table>

### `0x17` PATTable

```text
UInt32 EntryCount
repeat EntryCount times:
    Int32 ActionType
    Int32 ScriptIndex
Int32 DefaultAction
```

### `0x1A` Container

`SubType` selects the container variant (5=Sounds, 8=StockSounds). Payload is opaque and children are the only meaningful content.

### `0x1B` Dialog

Deeply nested — a topic table, each with its reply table, each with its line table.

```text
UInt32 HasAskAbout
UInt32 Character
UInt32 TopicCount
repeat TopicCount times:
    Bool   RemoveOnceDepleted
    UInt32 ReplyCount
    repeat ReplyCount times:
        UInt32            ConditionType
        ResourceReference ConditionRef
        ResourceReference ConditionScriptRef
        UInt32            ConditionReversed
        UInt32            field_88
        UInt32            MinChapter
        UInt32            MaxChapter
        UInt32            NoCaption
        Int32             NextDialogIndex
        ResourceReference NextScriptRef
        UInt32            LineCount
        repeat LineCount times:
            ResourceReference a
            ResourceReference b
```

### `0x1D` Speech

```text
String16 Subtitle                      -- the visible caption for a spoken line
Int32    Character                     -- speaker index
```

### `0x1E` Light`

```text
Vector3 Color
Vector3 Position
Vector3 Direction
Single  OuterConeAngle
Single  InnerConeAngle
Single  FalloffNear                    -- optional; only present if any bytes remain
Single  FalloffFar                     -- optional; only present if any bytes remain
```

The record's `SubType` byte selects the light kind (point vs spot vs directional) but its meaning is not decoded here.

### `0x20` BonesMesh

```text
String16 MeshFile                      -- reference to a companion .cir (or BIFF) file
```

### `0x21` Scroll

```text
UInt32 Coordinate
UInt32 field_30
UInt32 field_34
UInt32 BookmarkIndex
```

### `0x22` FMV

```text
String16 FmvFile
Bool     DiaryAddEntryOnPlay
UInt32   GameDisc
```

### `0x23` LipSync

Non-obvious stride-8 encoding: after the leading `ShapeCount`, each shape's ASCII byte lives 8 bytes apart, offset by 4 within its stride. In other words, at absolute offset `4 + i * 8 + 4` from the start of `Data` you find the ASCII character for shape `i`.

```text
UInt32 ShapeCount
byte[ShapeCount * 8] ShapeStream       -- byte at offset 4 + i*8 + 4 is shape i's ASCII code
UInt32 UnkCount
byte[UnkCount] _                       -- opaque trailer
```

### `0x24` AnimSoundTrigger

```text
UInt32 SoundTriggerTime
UInt32 SoundStockType
```

### `0x26` TextureSet

```text
String16 TextureFile
```

## Type IDs seen in the wild but not decoded

Some type IDs exist but their payloads are not interpreted; only the wrapper header and children are walked. Their names come from `TypeName`:

| TypeId | Name |
| --- | --- |
| `0x01` | Root |
| `0x02` | Level (structural reader treats this as a location) |
| `0x03` | Location |
| `0x0E` | AnimScript (wrapper around AnimScriptItem children) |
| `0x14` | KnowledgeSet (wrapper around Knowledge children) |
| `0x1A` | Container (opaque; only children matter) |
| `0x25` | String |

## Enum reference

Names for the raw `UInt32`s and `SubType` bytes described above. Values not listed are seen in the wild but unnamed.

### Activity (`Anim.Activity`)

| Value | Name |
| --- | --- |
| 0 | unspecified |
| 1 | idle |
| 2 | walk |
| 3 | talk |
| 6 | run |
| 10 | idle-action |

### Item.SubType

| Value | Name |
| --- | --- |
| 1 | GlobalItemTemplate |
| 2 | InventoryItem |
| 3 | LevelItemTemplate |
| 5 | StaticProp |
| 6 | AnimatedProp |
| 7 | BackgroundElement |
| 8 | Background |
| 10 | Model |

### Anim.SubType

| Value | Name |
| --- | --- |
| 1 | Images |
| 2 | Prop |
| 3 | Video |
| 4 | Skeleton |

### Image.SubType

| Value | Name |
| --- | --- |
| 2, 3 | Still |
| 4 | Text |

### Layer.SubType

| Value | Name |
| --- | --- |
| 1 | 2D |
| 2 | 3D |

### Path.SubType

| Value | Name |
| --- | --- |
| 1 | 2D |
| 2 | 3D |

### Knowledge.SubType

| Value | Name |
| --- | --- |
| 0 | Boolean |
| 2 | Integer |
| 3 | Integer2 |
| 4 | Reference |
| 5 | BooleanWithChild |

### Container.SubType

| Value | Name |
| --- | --- |
| 5 | Sounds |
| 8 | StockSounds |

### Script.SubType and Script.Type

Script.SubType (record variant):

| Value | Name |
| --- | --- |
| 4 | GameEvent |
| 5 | PlayerAction |
| 6 | Dialog |

Script.Type (first `UInt32` inside the payload):

| Value | Name |
| --- | --- |
| 0 | OnGameEvent / enabled |
| 1 | PassiveDialog |
| 2 | OnPlayerAction |
| 3 | Type3 (unknown) |
| 4 | Type4 (unknown) |

### Sound.SoundType and Sound.StockSoundType

`SoundType`:

| Value | Name |
| --- | --- |
| 0 | Voice |
| 1 | Effect |
| 2 | Music |

`StockSoundType`:

| Value | Name |
| --- | --- |
| 3 | Background |
| 5 | Stock |

### AnimScriptItem opcodes

| Opcode | Name |
| --- | --- |
| 0 | DisplayFrame |
| 1 | PlayAnimSound |
| 2 | GoToItem |
| 3 | DisplayRandomFrame |
| 4 | SleepRandomDuration |
| 5 | PlayStockSound |

## Known unknowns

- Every field explicitly named `field_XX` above — read to keep the payload cursor aligned; interpretation not derived. `XX` is the field's byte offset within the parent `Resource` struct in ScummVM's Stark engine source, kept as-is for cross-referencing.
- Any records whose type ID isn't decoded (see "seen in the wild but not decoded" above) — their payload is skipped entirely; only their children are walked.
- `Light.SubType` selects the light kind; the mapping isn't decoded here.
- `Sound.Enabled` is a `UInt32` and clearly not a simple bool — nonzero values may encode more than just "on".

## Decoder

- [`XrcDisplayDump`](../../src/TLJExplorer.Core/Formats/XrcDisplayDump.cs) — walks the whole tree and produces a text dump.
- [`XrcStructureReader`](../../src/TLJExplorer.Core/FileSystem/XrcStructureReader.cs) — reads a lightweight subset (Locations, Animation refs, Sound refs, Dialogue subtitles) for the VFS graft.
- [`XrcSceneModel`](../../src/TLJExplorer.Core/Formats/XrcSceneModel.cs) — interprets the resource graph for the scene compositor.
