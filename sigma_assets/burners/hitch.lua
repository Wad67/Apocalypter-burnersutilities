-- Burner's Utilities: a loose tow hitch, an item that clips onto a vehicle's hitch mount and then tows trailers as the
-- game's own hitches do (TrailerAttach: a sphere cast behind it finds a trailer, a click hitches and unhitches it).
--   hitch.item { mass = 8, roll = "Free" }   -- roll: "Locked" for a fifth wheel (the trailer can't roll on it)
local hitch = {}

function hitch.item(o)
  o = o or {}
  fsms {
    ID = { variables = { string = { ID = "trailer_hitch" } }, states = { ID = {} } },
    ItemName = {
      variables = { string = { ItemName = "" } },
      globalEvents = { "OverItem", "OffItem" },
      global = { OverItem = "over", OffItem = "off" },
      start = "off",
      states = {
        off = { actions = { { UiTextSetText = { gameObject = "{UI_ItemName}", text = "" } } } },
        over = { actions = { { UiTextSetText = { gameObject = "{UI_ItemName}", text = "{ItemName}" } } } },
      },
    },
    Attach = {
      variables = { gameObject = { CarPart = null } },
      states = {
        partMissing = {
          actions = {
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Point" } },
            {
              TriggerEventByLayer = { trigger = "OnTriggerEnter", layer = 8, sendEvent = "next", storeCollider = "{CarPart}" },
            },
          },
          transitions = { next = "checkChild" },
        },
        checkChild = {
          actions = {
            {
              GameObjectIsChildOf = { gameObject = "owner", isChildOf = "{Hand}", trueEvent = "next", falseEvent = "back" },
            },
          },
          transitions = { next = "Over", back = "partMissing" },
        },
        Over = {
          actions = {
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Wrench", everyFrame = true } },
            {
              TriggerEventByLayer = { trigger = "OnTriggerExit", layer = 8, sendEvent = "back", storeCollider = "{CarPart}" },
            },
            { GetButtonDown = { buttonName = "Use", sendEvent = "next" } },
          },
          transitions = { back = "partMissing", next = "Attach" },
        },
        Attach = {
          actions = {
            { PlaySound = { gameObject = "owner", clip = "asset:AudioClip/wrench1", volume = 1 } },
            { DestroyComponent = { gameObject = "owner", component = "UnityEngine.Rigidbody" } },
            { SendEvent = { eventTarget = "{PlayerCamera}/GrabItem", sendEvent = "not_Hold" } },
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Point" } },
            { SetParent = { gameObject = "owner", parent = "{CarPart}", worldPositionStays = true } },
            { SetLayer = { gameObject = "owner", layer = 8 } },
            { SetTag = { gameObject = "owner", tag = "vehPart" } },
            { NextFrameEvent = { sendEvent = "back" } },
          },
          transitions = { back = "partMissing" },
        },
      },
    },
    de_Attach = {
      globalEvents = { "de_Attach" },
      global = { de_Attach = "de Attach" },
      states = {
        idle = {},
        ["de Attach"] = {
          actions = {
            { SetLayer = { gameObject = "owner", layer = 9 } },
            { SetTag = { gameObject = "owner", tag = "vehPartRemoved" } },
          },
          transitions = { FINISHED = "idle" },
        },
      },
    },
    CheckTag = {
      variables = {
        float = { mass = 0 },
        gameObject = { owner = null },
        object = { Rigidbody = { type = "UnityEngine.Rigidbody", value = null } },
      },
      states = {
        start = {
          actions = {
            { GetMass = { gameObject = "owner", storeResult = "{mass}" } },
            { GetOwner = { storeGameObject = "{owner}" } },
            { EnableFSM = { gameObject = "owner", fsmName = "HitchUse", enable = false } },
            { NextFrameEvent = { sendEvent = "next" } },
          },
          transitions = { next = "vehPartRemoved" },
        },
        vehPartRemoved = {
          actions = {
            { GameObjectCompareTag = { gameObject = "{owner}", tag = "vehPart", trueEvent = "bolted" } },
            { SendEvent = { eventTarget = "root/TrailerAttach", sendEvent = "unhitch" } },
            { EnableFSM = { gameObject = "owner", fsmName = "HitchUse", enable = false } },
            { SetParent = { gameObject = "owner", parent = null, worldPositionStays = true } },
            { ColliderSetIsTrigger = { gameObject = "owner", isTrigger = false } },
            { EnableFSM = { gameObject = "owner", fsmName = "Attach", enable = true } },
            { HasRigidBody = { gameObject = "owner", found = "{}", foundEvent = "loose" } },
            {
              AddComponent = { gameObject = "owner", component = "UnityEngine.Rigidbody", storeComponent = "{Rigidbody}" },
            },
            { SetMass = { gameObject = "owner", mass = "{mass}" } },
            { SetProperties = { on = "{Rigidbody}", collisionDetectionMode = "Continuous" } },
          },
          transitions = { bolted = "vehPart", loose = "loose", FINISHED = "loose" },
        },
        loose = {
          actions = {
            {
              GameObjectCompareTag = { gameObject = "{owner}", tag = "vehPart", trueEvent = "next", everyFrame = true },
            },
          },
          transitions = { next = "vehPart" },
        },
        vehPart = {
          actions = {
            { DestroyComponent = { gameObject = "owner", component = "UnityEngine.Rigidbody" } },
            { ColliderSetIsTrigger = { gameObject = "owner", isTrigger = true } },
            { EnableFSM = { gameObject = "owner", fsmName = "Attach", enable = false } },
            { EnableFSM = { gameObject = "owner", fsmName = "HitchUse", enable = true } },
            {
              GameObjectCompareTag = { gameObject = "{owner}", tag = "vehPart", falseEvent = "next", everyFrame = true },
            },
          },
          transitions = { next = "vehPartRemoved" },
        },
      },
    },
    TrailerAttach = {
      params = { roll = "Free" },
      variables = {
        bool = { trailerAttached = false, trailerInRange_bool = false },
        string = { tag = "" },
        vector3 = {
          attacher_pos = { 0, 0, 0 },
          hitch_pos = { 0, 0, 0 },
          axis = { 0, 0, 0 },
          secondaryAxis = { 0, 0, 0 },
        },
        gameObject = { attacher = null, Trailer = null, Vehicle = null, Self = null },
        object = {
          ConfigurableJoint = { type = "UnityEngine.ConfigurableJoint", value = null },
          Rigidbody = { type = "UnityEngine.Rigidbody", value = null },
        },
      },
      globalEvents = { "hitchUse", "unhitch" },
      global = { unhitch = "detach" },
      start = "trailerOff",
      states = {
        trailerOff = {
          actions = {
            { use = "cast" },
            { BoolTest = { boolVariable = "{trailerInRange_bool}", isTrue = "next", everyFrame = true } },
          },
          transitions = { next = "canAttach" },
        },
        canAttach = {
          actions = {
            { use = "cast" },
            { BoolTest = { boolVariable = "{trailerInRange_bool}", isFalse = "back", everyFrame = true } },
          },
          transitions = { back = "trailerOff", hitchUse = "bolted" },
        },
        bolted = {
          actions = {
            { GetOwner = { storeGameObject = "{Self}" } },
            { GetTag = { gameObject = "{Self}", storeResult = "{tag}" } },
            {
              StringCompare = { stringVariable = "{tag}", compareTo = "vehPart", equalEvent = "yes", notEqualEvent = "no" },
            },
          },
          transitions = { yes = "free", no = "canAttach" },
        },
        free = {
          actions = {
            {
              HasComponent = {
                gameObject = "{Trailer}",
                component = "UnityEngine.ConfigurableJoint",
                trueEvent = "taken",
                falseEvent = "free",
                store = "{}",
              },
            },
          },
          transitions = { taken = "canAttach", free = "attach" },
        },
        attach = {
          actions = {
            { GetRoot = { gameObject = "owner", storeRoot = "{Vehicle}" } },
            { GetComponent = { gameObject = "{Vehicle}", storeComponent = "{Rigidbody}" } },
            { GetChild = { gameObject = "{Trailer}", childName = "attacher", storeResult = "{attacher}" } },
            { GetPosition = { gameObject = "{attacher}", vector = "{attacher_pos}", space = "World" } },
            {
              InverseTransformPoint = { gameObject = "{Trailer}", worldPosition = "{attacher_pos}", storeResult = "{attacher_pos}" },
            },
            { GetPosition = { gameObject = "owner", vector = "{hitch_pos}", space = "World" } },
            {
              InverseTransformPoint = { gameObject = "{Vehicle}", worldPosition = "{hitch_pos}", storeResult = "{hitch_pos}" },
            },
            { TransformDirection = { gameObject = "owner", localDirection = { 1, 0, 0 }, storeResult = "{axis}" } },
            {
              InverseTransformDirection = { gameObject = "{Trailer}", worldDirection = "{axis}", storeResult = "{axis}" },
            },
            {
              TransformDirection = { gameObject = "owner", localDirection = { 0, 1, 0 }, storeResult = "{secondaryAxis}" },
            },
            {
              InverseTransformDirection = { gameObject = "{Trailer}", worldDirection = "{secondaryAxis}", storeResult = "{secondaryAxis}" },
            },
            {
              AddComponent = {
                gameObject = "{Trailer}",
                component = "UnityEngine.ConfigurableJoint",
                storeComponent = "{ConfigurableJoint}",
              },
            },
            {
              SetProperties = {
                on = "{ConfigurableJoint}",
                connectedBody = "{Rigidbody}",
                anchor = "{attacher_pos}",
                axis = "{axis}",
                secondaryAxis = "{secondaryAxis}",
                autoConfigureConnectedAnchor = false,
                connectedAnchor = "{hitch_pos}",
                xMotion = "Locked",
                yMotion = "Locked",
                zMotion = "Locked",
                projectionDistance = 0.1,
                projectionAngle = 180,
                enableCollision = true,
              },
            },
          },
          transitions = { FINISHED = "angular" },
        },
        angular = {
          actions = {
            {
              SetProperties = {
                on = "{ConfigurableJoint}",
                angularXMotion = "Free",
                angularYMotion = "Free",
                angularZMotion = "$roll",
              },
            },
            { SetBoolValue = { boolVariable = "{trailerAttached}", boolValue = true } },
            { NextFrameEvent = { sendEvent = "next" } },
          },
          transitions = { next = "trailerOn" },
        },
        trailerOn = {
          actions = {
            {
              ObjectCompare = {
                objectVariable = "{ConfigurableJoint}",
                compareTo = "{}",
                equalEvent = "lost",
                storeResult = "{}",
                everyFrame = true,
              },
            },
          },
          transitions = { hitchUse = "detach", lost = "released" },
        },
        detach = {
          actions = {
            { BoolTest = { boolVariable = "{trailerAttached}", isFalse = "none" } },
            {
              ObjectCompare = { objectVariable = "{ConfigurableJoint}", compareTo = "{}", equalEvent = "gone", storeResult = "{}" },
            },
            { DestroyComponent = { gameObject = "{Trailer}", component = "UnityEngine.ConfigurableJoint" } },
          },
          transitions = { none = "trailerOff", gone = "released", FINISHED = "released" },
        },
        released = {
          actions = { { SetBoolValue = { boolVariable = "{trailerAttached}", boolValue = false } } },
          transitions = { FINISHED = "trailerOff" },
        },
      },
    },
    HitchUse = {
      variables = { bool = { attached = false, inRange = false }, gameObject = { trailer = null } },
      globalEvents = { "OverItem", "OffItem" },
      global = { OverItem = "over", OffItem = "off" },
      start = "off",
      states = {
        off = {
          actions = {
            { UiTextSetText = { gameObject = "{UI_ItemName_2}", text = "" } },
            { UiTextSetText = { gameObject = "{UI_ItemUse}", text = "" } },
          },
        },
        over = {
          actions = {
            {
              GetFsmBool = {
                gameObject = "owner",
                fsmName = "TrailerAttach",
                variableName = "trailerAttached",
                storeValue = "{attached}",
              },
            },
            { BoolTest = { boolVariable = "{attached}", isTrue = "attached", isFalse = "notAttached" } },
          },
          transitions = { attached = "attached", notAttached = "checkRange" },
        },
        checkRange = {
          actions = {
            {
              GetFsmBool = {
                gameObject = "owner",
                fsmName = "TrailerAttach",
                variableName = "trailerInRange_bool",
                storeValue = "{inRange}",
              },
            },
            { BoolTest = { boolVariable = "{inRange}", isTrue = "inRange", isFalse = "none" } },
          },
          transitions = { inRange = "checkFree", none = "none" },
        },
        checkFree = {
          actions = {
            {
              GetFsmGameObject = { gameObject = "owner", fsmName = "TrailerAttach", variableName = "Trailer", storeValue = "{trailer}" },
            },
            {
              HasComponent = {
                gameObject = "{trailer}",
                component = "UnityEngine.ConfigurableJoint",
                trueEvent = "taken",
                falseEvent = "free",
                store = "{}",
              },
            },
          },
          transitions = { taken = "taken", free = "inRange" },
        },
        taken = {
          actions = {
            {
              UiTextSetText = { gameObject = "{UI_ItemName_2}", text = "Trailer is hitched or tied to something else" },
            },
            {
              GetFsmBool = {
                gameObject = "owner",
                fsmName = "TrailerAttach",
                variableName = "trailerInRange_bool",
                storeValue = "{inRange}",
                everyFrame = true,
              },
            },
            { BoolTest = { boolVariable = "{inRange}", isFalse = "changed", everyFrame = true } },
          },
          transitions = { changed = "over" },
        },
        attached = {
          use = "prompt",
          with = { status = "Trailer hitched", action = "Unhitch: Left mouse button", watch = "trailerAttached" },
        },
        inRange = {
          use = "prompt",
          with = { status = "Trailer in reach", action = "Hitch: Left mouse button", watch = "trailerInRange_bool" },
        },
        none = {
          actions = {
            { UiTextSetText = { gameObject = "{UI_ItemName_2}", text = "No trailer in reach" } },
            {
              GetFsmBool = {
                gameObject = "owner",
                fsmName = "TrailerAttach",
                variableName = "trailerInRange_bool",
                storeValue = "{inRange}",
                everyFrame = true,
              },
            },
            { BoolTest = { boolVariable = "{inRange}", isTrue = "changed", everyFrame = true } },
          },
          transitions = { changed = "over" },
        },
        use = {
          actions = {
            { SendEvent = { eventTarget = "root/TrailerAttach", sendEvent = "hitchUse" } },
            { Wait = { time = 0.1 } },
          },
          transitions = { FINISHED = "over" },
        },
      },
    },
  }
  states {
    prompt = {
      actions = {
        { UiTextSetText = { gameObject = "{UI_ItemName_2}", text = "$status" } },
        { UiTextSetText = { gameObject = "{UI_ItemUse}", text = "$action" } },
        { GetMouseButtonDown = { button = "Left", sendEvent = "use", inUpdateOnly = true } },
        {
          GetFsmBool = {
            gameObject = "owner",
            fsmName = "TrailerAttach",
            variableName = "$watch",
            storeValue = "{attached}",
            everyFrame = true,
          },
        },
        { BoolTest = { boolVariable = "{attached}", isFalse = "changed", everyFrame = true } },
      },
      transitions = { use = "use", changed = "over" },
    },
  }
  actions {
    cast = {
      {
        SphereCast2 = {
          fromGameObject = "hitch_raycast",
          radius = 0.2,
          direction = { 0, 0, -1 },
          space = "Self",
          maxDistance = 0.2,
          storeDidHit = "{trailerInRange_bool}",
          storeHitObject = "{Trailer}",
          storeHitPoint = "{}",
          storeHitNormal = "{}",
          storeHitDistance = "{}",
          sphereCastCenter = "{}",
          repeatInterval = 2,
          layerMask = { 20 },
        },
      },
    },
  }
  local trailer = { use = "TrailerAttach" }
  if o.roll then trailer.with = { roll = o.roll } end
  return {
    layer = 9, tag = "vehPartRemoved", add = { "Rigidbody" },
    collider = { shape = "box", render = true },
    Rigidbody = { mass = o.mass or 8, collisionDetectionMode = "Continuous" },
    nodes = { hitch_raycast = { parent = "root", position = { 0, 0, 0.511 } } },
    playmaker = {
      ID = { use = "ID" }, ItemName = { use = "ItemName" }, Attach = { use = "Attach" }, de_Attach = { use = "de_Attach" },
      CheckTag = { use = "CheckTag" }, TrailerAttach = trailer, HitchUse = { use = "HitchUse" },
    },
  }
end

return hitch
