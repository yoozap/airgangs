import * as THREE from 'three';
import { OBJLoader } from 'three/addons/loaders/OBJLoader.js';
import { newGltfLoader } from './gltf.js';
import { clone as cloneSkeleton } from 'three/addons/utils/SkeletonUtils.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';

import { tagPropellers, finishPropellers } from './propeller.js';

// All selectable planes. GLBs come from tools-converted ~/Desktop/nature/jets
// models: real-world meters, nose toward -Z. Propellers are named "propeller*"
// where the modeller bothered -- three of them did -- and CUT OUT of the merged
// mesh by src/propeller.js where they did not. The Spitfire keeps its original
// OBJ path. `props` is how many the real aircraft has (default 1): the detector
// is told, so a four-engine Hercules is not satisfied by finding one.
//
// Physical data drives the realistic flight model (aero.js):
//   mass (kg) · tw (static thrust/weight) · gBreak (load factor that rips the
//   wings off) · clMax (max lift coefficient — sets the true stall speed) ·
//   prop (thrust falls off with airspeed like a propeller, not a jet).
//
// Hangar metadata: code/cls/desc for the info panel, stats are 0-100 bars
// [სიჩქარე, მანევრი, ჯავშანი, ცეცხლის ძალა].
// TANK or DPS is a property of the MACHINE, not of the pilot: "tank and dps
// are planes just man". The heavies -- bombers, transports, airliners -- are
// the TANKs that soak damage and carry troops; every fighter, racer and
// striker is a DPS. Each def below says which it is, and this is what the
// plane hall splits its two sides by (src/planehall.js).
export const planeRole = (p) =>
  ((typeof p === 'string' ? planeById(p) : p)?.role ?? 'DPS');
export const isTankPlane = (p) => planeRole(p) === 'TANK';

export const PLANES = [
  {
    id: 'spitfire',
    name: 'Spitfire',
    tagline: 'ლეგენდარული გამანადგურებელი · 590 კმ/სთ',
    file: '/models/spitfire.obj',
    thumb: '/textures/planes/spitfire.png',
    scale: 0.34, // 33-unit OBJ → ~11 m wingspan
    maxSpeed: 190,
    mass: 3000, tw: 2.8, gBreak: 11, clMax: 1.6, prop: true,
    role: 'DPS',
    code: 'MK-IX', cls: 'მებრძოლი', tag: 'FIGHTER', stats: [58, 88, 42, 55],
    desc: 'ლეგენდა, რომელიც ნეონის ეპოქასაც გადაურჩა. მოქნილი და მომტევებელი — ძველი თბილისის ვიწრო ქუჩებში მანევრირებისთვის იდეალურია.',
  },
  {
    id: 'kawasaki',
    name: 'Kawasaki Racer',
    tagline: 'სარბოლო თვითმფრინავი · 580 კმ/სთ',
    file: '/models/planes/kawasaki.glb',
    thumb: '/textures/planes/kawasaki.png',
    scale: 1,
    maxSpeed: 185,
    mass: 2500, tw: 2.6, gBreak: 9, clMax: 1.5, prop: true,
    role: 'DPS',
    code: 'KW-R', cls: 'მზვერავი', tag: 'SCOUT', stats: [55, 92, 28, 38],
    desc: 'მსუბუქი სარბოლო აპარატი. თითქმის უჯავშნოა, მაგრამ მტკვრის ხიდების ქვეშ გაძრომისას მას ვერავინ დაეწევა.',
  },
  {
    id: 'p100',
    name: 'P-100 Avenger',
    tagline: 'შემტევი გამანადგურებელი · 700 კმ/სთ',
    file: '/models/planes/p100.glb',
    thumb: '/textures/planes/p100.png',
    scale: 1,
    maxSpeed: 220,
    mass: 5200, tw: 3.0, gBreak: 10, clMax: 1.5, prop: true,
    role: 'DPS',
    code: 'P-100', cls: 'მოიერიშე', tag: 'STRIKER', stats: [66, 72, 58, 74],
    desc: 'შემტევი გამანადგურებელი ორმაგი ქვემეხით. დაბალანსებული ბრძოლისთვის ნებისმიერ სიმაღლეზე — ნარიყალადან ტელეანძამდე.',
  },
  {
    id: 'f104',
    name: 'F-104 Starfighter',
    tagline: 'ზებგერითი გამანადგურებელი · 2137 კმ/სთ',
    file: '/models/planes/f104.glb',
    thumb: '/textures/planes/f104.png',
    scale: 1,
    maxSpeed: 420,
    // The widow-maker: razor wing, lands at ~280 km/h — treat it with respect.
    mass: 9300, tw: 3.6, gBreak: 8.5, clMax: 1.25,
    role: 'DPS',
    code: 'F-104', cls: 'ჩამჭრელი', tag: 'INTERCEPT', stats: [98, 40, 45, 68],
    desc: '«ქვრივების მჭედელი». სამართებელივით ბასრი ფრთა და ზებგერითი სისწრაფე — ერთი შეცდომა და კედელი შენია.',
  },
  {
    id: 'a7',
    name: 'A-7 Corsair II',
    tagline: 'მოიერიშე თვითმფრინავი · 1112 კმ/სთ',
    file: '/models/planes/a7.glb',
    thumb: '/textures/planes/a7.png',
    scale: 1,
    maxSpeed: 260,
    mass: 11000, tw: 3.0, gBreak: 9.5, clMax: 1.7,
    role: 'DPS',
    code: 'A-7', cls: 'მოიერიშე', tag: 'STRIKER', stats: [74, 58, 72, 86],
    desc: 'მიწასთან მუშაობის ოსტატი. სქელი ჯავშანი და მძიმე დანადგარი — ბანდების საყვარელი იარაღი სახურავების დასაშლელად.',
  },
  {
    id: 'b58',
    name: 'B-58 Hustler',
    tagline: 'ზებგერითი ბომბდამშენი · 2228 კმ/სთ',
    file: '/models/planes/b58.glb',
    thumb: '/textures/planes/b58.png',
    scale: 1,
    maxSpeed: 400,
    mass: 55000, tw: 3.2, gBreak: 4.5, clMax: 1.3,
    role: 'TANK',
    code: 'B-58', cls: 'ბომბდამშენი', tag: 'BOMBER', stats: [95, 26, 60, 92],
    desc: 'ზებგერითი დელტა-ბომბდამშენი. ფრთხილად მოუხვიე — მაგრამ როცა გაჩერდება მიზანზე, მთელი უბანი ინგრევა.',
  },
  {
    id: 'rq180',
    name: 'RQ-180',
    tagline: 'სტელს დრონი · 900 კმ/სთ',
    file: '/models/planes/rq180.glb',
    thumb: '/textures/planes/rq180.png',
    scale: 1,
    maxSpeed: 260,
    mass: 14000, tw: 2.8, gBreak: 6, clMax: 1.4,
    role: 'TANK',
    code: 'RQ-180', cls: 'პროტოტიპი', tag: 'STEALTH', stats: [80, 62, 34, 48],
    desc: 'ექსპერიმენტული სტელს-ფრთა. რადარები მას ვერ ხედავენ — მოწინააღმდეგე მხოლოდ აფეთქებას გაიგებს.',
  },
  {
    id: 'globemaster',
    name: 'C-17 Globemaster III',
    tagline: 'მძიმე სატრანსპორტო · 830 კმ/სთ',
    file: '/models/planes/globemaster.glb',
    thumb: '/textures/planes/globemaster.png',
    scale: 1,
    maxSpeed: 230,
    mass: 140000, tw: 2.2, gBreak: 3.8, clMax: 2.3,
    role: 'TANK',
    code: 'C-17', cls: 'მძიმე', tag: 'HEAVY', stats: [62, 18, 96, 30],
    desc: 'მფრინავი ციხესიმაგრე. ნელია და უზარმაზარი, მაგრამ მისი კორპუსი ტყვიების სეტყვას ისე იტანს, როგორც წვიმას.',
  },
  // ---- tools/build_planes.py wave: fighters & trainers (DPS) ----
  {
    id: 'a10', name: 'A-10 Thunderbolt II', tagline: 'მოიერიშე ჯავშანთმფრენი · 706 კმ/სთ',
    file: '/models/planes/a10.glb', thumb: '/textures/planes/a10.png', scale: 1, maxSpeed: 210,
    mass: 11300, tw: 2.6, gBreak: 9, clMax: 1.9,
    role: 'DPS',
    code: 'A-10', cls: 'მოიერიშე', tag: 'STRIKER', stats: [55, 60, 90, 98],
    desc: 'მფრინავი ქვემეხი ტიტანის აბაზანაში. ნელია, მაგრამ რასაც მისი GAU-8 შეეხება, აღარ დგება.',
  },
  {
    id: 'aerobatic', name: 'Extra 330', tagline: 'აკრობატიული · 407 კმ/სთ',
    file: '/models/planes/aerobatic.glb', thumb: '/textures/planes/aerobatic.png', scale: 1, maxSpeed: 170,
    mass: 950, tw: 3.4, gBreak: 12, clMax: 1.8, prop: true,
    role: 'DPS',
    code: 'EX-330', cls: 'აკრობატი', tag: 'ACRO', stats: [40, 100, 10, 8],
    desc: 'საჰაერო ცირკის ვარსკვლავი. ±10G და უსასრულო ტრიალი — ვიწრო ეზოებში მას ვერავინ დაიჭერს.',
  },
  {
    id: 'cessna310', name: 'Cessna 310', tagline: 'მსუბუქი ორძრავა · 380 კმ/სთ',
    file: '/models/planes/cessna310.glb', thumb: '/textures/planes/cessna310.png', scale: 1, maxSpeed: 160,
    yaw: -Math.PI / 2, // orientation fix — GLB stored nose-west (tools/probe_props.mjs)
    mass: 2500, tw: 1.8, gBreak: 6, clMax: 1.6, prop: true, props: 2,
    // Blades welded into the hull: measured with tools/_probe_hub.mjs.
    propHubs: [[1.94, 1.50, 3.06], [-1.94, 1.50, 3.06]], propRadius: 0.78, propDepth: 0.34,
    role: 'DPS',
    code: 'C-310', cls: 'სამოქალაქო', tag: 'CIVIL', stats: [35, 55, 25, 10],
    desc: 'მშვიდობიანი ამხანაგი ფრთის წვერის ავზებით. იარაღი თითქმის არ აქვს — სამაგიეროდ ბენზინზე იაფად დადის.',
  },
  {
    id: 't28', name: 'T-28 Trojan', tagline: 'სასწავლო-საბრძოლო · 552 კმ/სთ',
    file: '/models/planes/t28.glb', thumb: '/textures/planes/t28.png', scale: 1, maxSpeed: 185,
    mass: 3856, tw: 2.4, gBreak: 9, clMax: 1.7, prop: true,
    role: 'DPS',
    code: 'T-28', cls: 'სასწავლო', tag: 'TRAINER', stats: [48, 70, 35, 40],
    desc: 'სკოლის მერხი, რომელიც ვიეტნამში ომში წავიდა. პატიოსანი, გამძლე და მოთმინებით სავსე — დამწყები გმირის საუკეთესო მეგობარი.',
  },
  {
    id: 'f4f', name: 'F4F Wildcat', tagline: 'გემბანის მებრძოლი · 533 კმ/სთ',
    file: '/models/planes/f4f.glb', thumb: '/textures/planes/f4f.png', scale: 1, maxSpeed: 180,
    yaw: -Math.PI / 2, // orientation fix — GLB stored nose-west (tools/probe_props.mjs)
    mass: 3200, tw: 2.3, gBreak: 9.5, clMax: 1.8, prop: true,
    role: 'DPS',
    code: 'F4F', cls: 'მებრძოლი', tag: 'FIGHTER', stats: [45, 68, 48, 50],
    desc: 'წყნარი ოკეანის ველური კატა. მოკლე ფრთები, ჯიუტი ხასიათი — და ბრძოლას არასდროს გაურბის.',
  },
  {
    id: 'p47', name: 'P-47 Thunderbolt', tagline: 'მძიმე მებრძოლი · 697 კმ/სთ',
    file: '/models/planes/p47.glb', thumb: '/textures/planes/p47.png', scale: 1, maxSpeed: 205,
    yaw: Math.PI, // orientation fix — GLB stored tail-first (tools/probe_props.mjs)
    mass: 6600, tw: 2.5, gBreak: 10, clMax: 1.6, prop: true,
    role: 'DPS',
    code: 'P-47', cls: 'მებრძოლი', tag: 'FIGHTER', stats: [60, 62, 70, 72],
    desc: '«ქილა» — რვა ტყვიამფრქვევი და ჯავშანი, რომელსაც პიკირებაში ვერავინ დაეწევა. ძირს დაცემაც კი ეშინია მისი.',
  },
  {
    id: 'p51', name: 'P-51 Mustang', tagline: 'სადაზვერვო-მებრძოლი · 703 კმ/სთ',
    file: '/models/planes/p51.glb', thumb: '/textures/planes/p51.png', scale: 1, maxSpeed: 210,
    mass: 5500, tw: 2.6, gBreak: 10, clMax: 1.65, prop: true,
    // The model is a Mustang with a spinner and no blades at all, so it gets
    // a real one: 3.40 m Hamilton Standard, four blades, rooted on the
    // spinner the model does have (tools/_probe_hub.mjs).
    propMake: { hub: [0, 1.57, 4.95], radius: 1.70, blades: 4 },
    role: 'DPS',
    code: 'P-51', cls: 'მებრძოლი', tag: 'FIGHTER', stats: [66, 78, 45, 60],
    desc: 'კადილაკი ცაში. სწრაფი, ლამაზი და შორსმფრენი — ესკორტის მეფე, რომელიც მტკვრის ხეობასაც ბოლომდე გაჰყვება.',
  },
  {
    id: 'hsp75', name: 'Henschel Hs P.75', tagline: 'ქანარდიანი პროტოტიპი · 780 კმ/სთ',
    file: '/models/planes/hsp75.glb', thumb: '/textures/planes/hsp75.png', scale: 1, maxSpeed: 220,
    yaw: Math.PI, // orientation fix — GLB stored tail-first (tools/probe_props.mjs)
    mass: 5200, tw: 2.7, gBreak: 8.5, clMax: 1.5, prop: true,
    role: 'DPS',
    code: 'P.75', cls: 'პროტოტიპი', tag: 'PROTO', stats: [58, 66, 40, 45],
    desc: 'ქაღალდიდან გამოქცეული ნახაზი: ფრთა უკან, საჭე წინ, ხრახნი კუდში. არავინ იცის როგორ დაფრინავს — მაგრამ დაფრინავს.',
  },
  {
    id: 'neonfly', name: 'El NeonFly', tagline: 'სარბოლო ჯეტი · 800 კმ/სთ',
    file: '/models/planes/neonfly.glb', thumb: '/textures/planes/neonfly.png', scale: 1, maxSpeed: 240,
    roll: Math.PI / 2, // orientation fix — GLB stored banked on its wing
    mass: 3100, tw: 3.2, gBreak: 11, clMax: 1.6,
    role: 'DPS',
    code: 'NEON', cls: 'სარბოლო', tag: 'RACER', stats: [72, 95, 20, 15],
    desc: 'შეჯიბრის ჯეტი ნეონის ქალაქისთვის. მსუბუქი, ავხორცი და თავხედი — ჰაერშივე იცვლის მიმართულებას, როგორც ფიქრს.',
  },
  {
    id: 'javelin', name: 'Gloster Javelin', tagline: 'ღამის ჩამჭრელი · 1140 კმ/სთ',
    file: '/models/planes/javelin.glb', thumb: '/textures/planes/javelin.png', scale: 1, maxSpeed: 265,
    yaw: Math.PI, // orientation fix — GLB stored tail-first (tools/probe_props.mjs)
    mass: 17500, tw: 2.9, gBreak: 7.5, clMax: 1.45,
    role: 'DPS',
    code: 'FAW-9', cls: 'ჩამჭრელი', tag: 'INTERCEPT', stats: [70, 55, 60, 62],
    desc: 'ბრიტანული დელტა ღამის ცისთვის. ორი ძრავა, ორი კაცი და ერთი მიზანი — რომ ღამით ბომბდამშენი არ გაგეპაროს.',
  },
  {
    id: 'mig21', name: 'MiG-21 Fishbed', tagline: 'ზებგერითი გამანადგურებელი · 2175 კმ/სთ',
    file: '/models/planes/mig21.glb', thumb: '/textures/planes/mig21.png', scale: 1, maxSpeed: 415,
    // The three-jets-in-one-scene file (see `strip` in tools/build_planes.py)
    // leaves a centreline profile full of holes, so the measurement found a
    // 9 cm "cockpit" behind the canopy and stood the pilot on the spine.
    // Read off tools/_probe_cockpit.mjs: the canopy is at z -3.9, top 1.13.
    cockpit: { z: -3.9, closed: true },
    mass: 8800, tw: 3.4, gBreak: 8.5, clMax: 1.3,
    role: 'DPS',
    code: 'МиГ-21', cls: 'გამანადგურებელი', tag: 'FIGHTER', stats: [92, 60, 40, 58],
    desc: 'მფრინავი კალაშნიკოვი — მარტივი, იაფი და ულმობელი. ნახევარი მსოფლიო ამით სწავლობდა ზებგერით ფრენას.',
  },
  {
    id: 'mig21nk', name: 'MiG-21PFM', tagline: 'ზებგერითი გამანადგურებელი · 2175 კმ/სთ',
    file: '/models/planes/mig21nk.glb', thumb: '/textures/planes/mig21nk.png', scale: 1, maxSpeed: 410,
    mass: 8800, tw: 3.4, gBreak: 8.5, clMax: 1.3,
    role: 'DPS',
    code: 'ПФМ-415', cls: 'გამანადგურებელი', tag: 'FIGHTER', stats: [90, 58, 42, 56],
    desc: 'ჩრდილოეთის ნაცრისფერი ძმა, ბორტნომერი 415. იგივე რაკეტა ფრთებით — ოღონდ სხვისი დროშით და მკაცრი ხასიათით.',
  },
  {
    id: 'tornado', name: 'Panavia Tornado', tagline: 'ცვალებადი ფრთა · 2400 კმ/სთ',
    file: '/models/planes/tornado.glb', thumb: '/textures/planes/tornado.png', scale: 1, maxSpeed: 400,
    yaw: -0.658, // orientation fix — GLB sits 37.7deg off the fuselage axis (tools/probe_props.mjs)
    mass: 20000, tw: 3.1, gBreak: 7.5, clMax: 1.7,
    role: 'DPS',
    code: 'GR-1', cls: 'მოიერიშე', tag: 'STRIKER', stats: [88, 64, 62, 80],
    desc: 'ევროპული ქარიშხალი საზეიმო შეფერილობით. ფრთებს კეცავს და მიწასთან აკრული მიქრის — რადარი ვერც ამჩნევს.',
  },
  {
    id: 'su33', name: 'Su-33 Flanker-D', tagline: 'გემბანის გამანადგურებელი · 2300 კმ/სთ',
    file: '/models/planes/su33.glb', thumb: '/textures/planes/su33.png', scale: 1, maxSpeed: 420,
    mass: 24000, tw: 3.5, gBreak: 9, clMax: 1.9,
    role: 'DPS',
    code: 'Су-33', cls: 'გამანადგურებელი', tag: 'FIGHTER', stats: [94, 86, 58, 78],
    desc: 'ზღვის ფლანკერი ქანარდებით. მძიმეა, მაგრამ კობრასავით იხრება — ჰაერში მასთან ჭიდაობა ცუდი იდეაა.',
  },
  {
    id: 'f15', name: 'F-15C Eagle', tagline: 'უზენაესობის მებრძოლი · 2655 კმ/სთ',
    file: '/models/planes/f15.glb', thumb: '/textures/planes/f15.png', scale: 1, maxSpeed: 440,
    mass: 20200, tw: 3.6, gBreak: 9, clMax: 1.6,
    role: 'DPS',
    code: 'F-15C', cls: 'გამანადგურებელი', tag: 'FIGHTER', stats: [97, 80, 60, 84],
    desc: '104:0 — ეს მისი ანგარიშია საჰაერო ბრძოლებში. არწივი, რომელიც ჯერ არავის ჩამოუგდია.',
  },
  {
    id: 'f117', name: 'F-117 Nighthawk', tagline: 'სტელს-მოიერიშე · 993 კმ/სთ',
    file: '/models/planes/f117.glb', thumb: '/textures/planes/f117.png', scale: 1, maxSpeed: 250,
    mass: 23800, tw: 2.4, gBreak: 6, clMax: 1.1,
    role: 'DPS',
    code: 'F-117', cls: 'სტელსი', tag: 'STEALTH', stats: [70, 35, 45, 66],
    desc: 'ღამის შავი დანა. რადარისთვის უჩინარია, თვალისთვის — კოშმარი. ფრთხილად: ეს დანა ორივე მხარეს ჭრის.',
  },
  // ---- tools/build_planes.py wave: big machines (TANK) ----
  {
    id: 'lancaster', name: 'Avro Lancaster', tagline: 'მძიმე ბომბდამშენი · 454 კმ/სთ',
    file: '/models/planes/lancaster.glb', thumb: '/textures/planes/lancaster.png', scale: 1, maxSpeed: 175,
    mass: 29000, tw: 2.0, gBreak: 5, clMax: 1.9, prop: true, props: 4,
    // Four Merlins, blades welded into one continuous surface — measured
    // station by station with tools/_probe_hub.mjs.
    propHubs: [[3.88, 4.16, 7.44], [-3.88, 4.16, 7.44],
               [8.09, 4.01, 6.91], [-8.09, 4.01, 6.91]],
    propRadius: 2.30, propDepth: 0.95,
    role: 'TANK',
    code: 'LANC', cls: 'ბომბდამშენი', tag: 'BOMBER', stats: [40, 25, 70, 85],
    desc: 'ღამის ლეგენდა ოთხი «მერლინით». კუპოლები წრიპინებენ, ბომბსაცავი კი სავსეა — ძველი სკოლის მძიმე არგუმენტი.',
  },
  {
    id: 'h6k', name: 'Xian H-6K', tagline: 'სარაკეტო ბომბდამშენი · 1050 კმ/სთ',
    file: '/models/planes/h6k.glb', thumb: '/textures/planes/h6k.png', scale: 1, maxSpeed: 250,
    yaw: Math.PI, // orientation fix — GLB stored tail-first (tools/probe_props.mjs)
    mass: 79000, tw: 2.5, gBreak: 4.5, clMax: 1.5,
    role: 'TANK',
    code: 'H-6K', cls: 'ბომბდამშენი', tag: 'BOMBER', stats: [72, 22, 68, 90],
    desc: 'ძველი Ту-16-ის აღმოსავლური რეინკარნაცია. ექვსი ფრთისქვეშა რაკეტა — და უბანი აღარ არის უბანი.',
  },
  {
    id: 'tu144', name: 'Tu-144', tagline: 'ზებგერითი ლაინერი · 2500 კმ/სთ',
    file: '/models/planes/tu144.glb', thumb: '/textures/planes/tu144.png', scale: 1, maxSpeed: 430,
    yaw: -0.702, // orientation fix — GLB sits 40.2deg off the fuselage axis (tools/probe_props.mjs)
    mass: 180000, tw: 3.0, gBreak: 4, clMax: 1.4,
    role: 'TANK',
    code: 'Ту-144', cls: 'ზებგერითი', tag: 'SUPERSONIC', stats: [99, 15, 55, 20],
    desc: '«კონკორდსკი» — სამგზავრო თვითმფრინავი, რომელიც ტყვიაზე სწრაფად დაფრინავს. მოხვევა? ჯერ ბათუმამდე გაფრინდი, მერე მოუხვიე.',
  },
  {
    id: 'xb35', name: 'Northrop XB-35', tagline: 'მფრინავი ფრთა · 630 კმ/სთ',
    file: '/models/planes/xb35.glb', thumb: '/textures/planes/xb35.png', scale: 1, maxSpeed: 200,
    mass: 82000, tw: 2.2, gBreak: 4.5, clMax: 1.8, prop: true, props: 4,
    role: 'TANK',
    code: 'XB-35', cls: 'პროტოტიპი', tag: 'PROTO', stats: [55, 30, 75, 82],
    desc: 'ფუსელაჟი? კუდი? ზედმეტი დეტალებია. მხოლოდ ფრთა, ოთხი მბრუნავი ხრახნი და 52 მეტრი ჩრდილი ქალაქზე.',
  },
  {
    id: 'carpet', name: 'Carpet Bomber', tagline: 'ხალიჩა-ბომბდამშენი · 480 კმ/სთ',
    file: '/models/planes/carpet.glb', thumb: '/textures/planes/carpet.png', scale: 1, maxSpeed: 190,
    cockpit: { z: -11.5, closed: true }, // the measurement found the fin, not the nose
    mass: 30000, tw: 2.1, gBreak: 4.8, clMax: 1.8, prop: true, props: 4,
    role: 'TANK',
    code: 'LCB', cls: 'ბომბდამშენი', tag: 'BOMBER', stats: [45, 28, 65, 88],
    desc: 'სახელი თავად ამბობს: სადაც ეს ჩაივლის, იქ ბალახიც ხელახლა ითესება. ფრთხილად შენს უბანთან.',
  },
  {
    id: 'b737', name: 'Boeing 737-300', tagline: 'ავიალაინერი · 880 კმ/სთ',
    file: '/models/planes/b737.glb', thumb: '/textures/planes/b737.png', scale: 1, maxSpeed: 235,
    mass: 56000, tw: 2.3, gBreak: 4, clMax: 2.0,
    role: 'TANK',
    code: 'B-737', cls: 'ავიალაინერი', tag: 'LINER', stats: [66, 30, 62, 10],
    desc: 'სამოქალაქო კლასიკა. 148 სავარძელი, უფასო წვენი და გმირი შტურვალთან — რას იზამ, ომია.',
  },
  {
    id: 'c130', name: 'C-130J Hercules', tagline: 'ტაქტიკური სატრანსპორტო · 670 კმ/სთ',
    file: '/models/planes/c130.glb', thumb: '/textures/planes/c130.png', scale: 1, maxSpeed: 205,
    mass: 70000, tw: 2.2, gBreak: 4.2, clMax: 2.1, prop: true, props: 4,
    propHubs: [[5.11, 5.99, 6.09], [-5.11, 5.99, 6.09],
               [10.31, 6.11, 6.00], [-10.31, 6.11, 6.00]],
    propRadius: 2.20, propDepth: 0.80,
    role: 'TANK',
    code: 'C-130J', cls: 'სატრანსპორტო', tag: 'TRANSPORT', stats: [52, 34, 80, 25],
    desc: 'ჰერკულესი — სახელი ავალდებულებს. ზის იქ, სადაც ბილიკიც არ არის, და ოთხი ხრახნით ისევ ცაში ადის.',
  },
  {
    id: 'yc130', name: 'YC-130 Prototype', tagline: 'ჰერკულესის წინაპარი · 640 კმ/სთ',
    file: '/models/planes/yc130.glb', thumb: '/textures/planes/yc130.png', scale: 1, maxSpeed: 200,
    mass: 62000, tw: 2.2, gBreak: 4.2, clMax: 2.1, prop: true, props: 4,
    propHubs: [[5.19, 4.27, 6.73], [-5.19, 4.27, 6.73],
               [10.35, 4.57, 6.73], [-10.35, 4.57, 6.73]],
    propRadius: 2.08, propDepth: 0.88,
    role: 'TANK',
    code: 'YC-130', cls: 'პროტოტიპი', tag: 'PROTO', stats: [50, 36, 74, 22],
    desc: 'პირველი ჰერკულესი — ჯერ კიდევ ქარხნული სუნით. ისტორია ფრთებზე: 1954 წლის 23 აგვისტო, პირველი აფრენა.',
  },
  {
    id: 'cargobro', name: 'Cargo Bro', tagline: 'სადესანტო ტრანსპორტი · 700 კმ/სთ',
    file: '/models/planes/cargobro.glb', thumb: '/textures/planes/cargobro.png', scale: 1, maxSpeed: 210,
    mass: 65000, tw: 2.3, gBreak: 4.5, clMax: 2.0, prop: true, props: 0,
    role: 'TANK',
    code: 'CARGO', cls: 'სატრანსპორტო', tag: 'TRANSPORT', stats: [55, 32, 85, 30],
    desc: 'სადესანტო ძმაკაცი. უკანა რამპა იღება, ასი მებრძოლი გადმოდის — შენ კი უბრალოდ წრეებს ხაზავ ქალაქზე.',
  },
  {
    id: 'il76', name: 'Ilyushin Il-76', tagline: 'მძიმე სატრანსპორტო · 900 კმ/სთ',
    file: '/models/planes/il76.glb', thumb: '/textures/planes/il76.png', scale: 1, maxSpeed: 235,
    mass: 140000, tw: 2.3, gBreak: 4, clMax: 2.2,
    role: 'TANK',
    code: 'Ил-76', cls: 'სატრანსპორტო', tag: 'TRANSPORT', stats: [64, 24, 88, 28],
    desc: 'საბჭოთა სამუშაო ცხენი სრული ინტერიერით. კაბინაში შუშის ცხვირია — ჩამოჯექი და უყურე, როგორ მიცურავს ქალაქი.',
  },
  {
    id: 'b767', name: 'Boeing 767', tagline: 'შორეული ლაინერი · 913 კმ/სთ',
    file: '/models/planes/b767.glb', thumb: '/textures/planes/b767.png', scale: 1, maxSpeed: 240,
    mass: 142000, tw: 2.3, gBreak: 3.8, clMax: 2.1,
    role: 'TANK',
    code: 'B-767', cls: 'ავიალაინერი', tag: 'LINER', stats: [68, 22, 70, 5],
    desc: 'ორმილიანი ტრანსატლანტიკური დარბაისელი. დინჯად მოხვევს, დინჯად დაჯდება — და მთელი უბანი ჩრდილში ეხვევა.',
  },
  {
    id: 'awacs', name: 'Air Defense Sentinel', tagline: 'სადარაჯო თვითმფრინავი · 850 კმ/სთ',
    file: '/models/planes/awacs.glb', thumb: '/textures/planes/awacs.png', scale: 1, maxSpeed: 230,
    mass: 125000, tw: 2.3, gBreak: 4, clMax: 2.0,
    role: 'TANK',
    code: 'AWACS', cls: 'სადარაჯო', tag: 'RADAR', stats: [60, 26, 78, 35],
    desc: 'ცის დიდი ძმა. ყველას ხედავს, ყველას ისმენს — შენს უბანში ბეღურაც ვერ აფრინდება მისი ნებართვის გარეშე.',
  },
  {
    id: 'an225', name: 'Antonov An-225 Mriya', tagline: 'მსოფლიოში უდიდესი · 850 კმ/სთ',
    file: '/models/planes/an225.glb', thumb: '/textures/planes/an225.png', scale: 1, maxSpeed: 235,
    mass: 285000, tw: 2.1, gBreak: 3.2, clMax: 2.3,
    role: 'TANK',
    code: 'Ан-225', cls: 'გიგანტი', tag: 'GIANT', stats: [62, 8, 100, 15],
    desc: '«მრია» — ოცნება. ექვსი ძრავა, 32 ბორბალი, 88 მეტრი ფრთა. როცა ეს დაჯდება, აეროპორტი ორ დღეს ივსება ხალხით.',
  },
];

export const planeById = (id) => PLANES.find((p) => p.id === id) ?? PLANES[0];

// The Spitfire OBJ references a missing .mtl, so materials are assigned by name.
// Part names are Turkish: gövde = fuselage, cam = canopy glass, pervane = propeller,
// teker/lastik = wheels, jant = rims, koltuk = seat, kanat = wings.
const SPITFIRE_MATERIALS = {
  'gövde': { color: 0x5a6e42, metalness: 0.35, roughness: 0.55 }, // RAF green
  'gövdeparca': { color: 0x77683d, metalness: 0.35, roughness: 0.55 }, // earth camo
  'cam': { color: 0x9fd2ff, metalness: 0.1, roughness: 0.05, transparent: true, opacity: 0.4 },
  'cam2': { color: 0x9fd2ff, metalness: 0.1, roughness: 0.05, transparent: true, opacity: 0.4 },
  'pervane': { color: 0x2a2a2a, metalness: 0.2, roughness: 0.7 },
  'jant': { color: 0x8a8f98, metalness: 0.8, roughness: 0.35 },
  'lastikön': { color: 0x151515, metalness: 0.0, roughness: 0.95 },
  'arkateker': { color: 0x151515, metalness: 0.0, roughness: 0.95 },
  'arkatekertucu': { color: 0x555b64, metalness: 0.7, roughness: 0.4 },
  'koltuk': { color: 0x6b4a2f, metalness: 0.1, roughness: 0.8 },
  'zevkcubugu': { color: 0x222222, metalness: 0.3, roughness: 0.6 },
};

const cache = new Map();

async function loadSpitfireModel() {
  const model = await new OBJLoader().loadAsync('/models/spitfire.obj');
  model.traverse((child) => {
    if (!child.isMesh) return;
    const mats = Array.isArray(child.material) ? child.material : [child.material];
    child.material = mats.map((m) => {
      const def = SPITFIRE_MATERIALS[m?.name] ?? { color: 0x5a6e42, metalness: 0.3, roughness: 0.6 };
      return new THREE.MeshStandardMaterial({ name: m?.name ?? '', ...def });
    });
    if (child.material.length === 1) child.material = child.material[0];

    // Re-pivot the propeller so it can spin around its own center.
    if (child.name.startsWith('pervane')) {
      child.geometry.computeBoundingBox();
      const center = new THREE.Vector3();
      child.geometry.boundingBox.getCenter(center);
      child.geometry.translate(-center.x, -center.y, -center.z);
      child.position.copy(center);
      child.userData.isPropeller = true;
    }
  });
  return model;
}

// ---- glazing the hood -------------------------------------------------------
// Half these models paint the canopy on as a solid black lump -- the aerobatic
// plane's `cocpit_black` is opaque, and so is the MiG's hood -- so a pilot
// sitting under it is a pilot nobody can see. The user wants the personage
// visible in there ("plane hood must be glased, that i need to see my
// personage inside hood"), so every canopy is turned into real glass, with
// the same recipe the Spitfire's own `cam` material already uses.
//
// It runs on the CACHED model, before any clone is taken, so it happens once
// per aircraft and every copy of it is glazed -- and, because it runs before
// findCockpit measures, a glazed hood is read as a CLOSED canopy: the pilot
// has to fit underneath it, which is the whole point of being able to see in.
const HOOD_RX = /glass|canopy|cocpit|cockpit|kabina|cabina|hood|windscreen|^cam$|^cam[0-9_]|_cam$/i;
// An interior, a seat or a passenger cabin is not a hood. Glazing those turns
// an aeroplane into a fish tank.
// NOT `body`: glTF names half the meshes in these files `Body_something`, and
// the aerobatic plane's hood is `Body_cocpit_black_0` — excluding it on that
// word left the one plane the user pointed at still wearing a black lid. The
// size and centreline checks below are what keep the fuselage skin out.
const NOT_HOOD_RX = /interior|seat|koltuk|chair|pilot|frame|rahmen/i;

// THE INSIDE OF THE AEROPLANE. A hull is one skin with its faces turned
// outwards, so from in the cockpit you look straight THROUGH the machine and
// out the other side: the furniture reads as hanging in daylight, which is
// what the user saw — "it havent some material between interior and body ...
// it looks like interior is in air".
//
// The first fix was a back-faced box inside the fuselage. It was a bad idea:
// a box is not the shape of an aeroplane, so wherever the hull tapered in
// past it its far wall showed OUTSIDE the machine — "u put fucken square
// inside plane". There is no sizing of a box that is right.
//
// What is right is the hull itself: stop culling its back faces and the
// cockpit is lined by the real skin, exactly shaped, no gaps and no new
// geometry. Three.js flips the normal on a back face, so it shades properly
// from inside. Only the opaque airframe — the glass is left alone, or you
// see the far side of the canopy through the near side.
// THREE TRIES AT A SEPARATE LINER, ALL WRONG. A back-faced box showed outside
// the fuselage wherever the hull tapered in past it. A trough lofted from the
// hull's own cross-sections looked right on the aeroplane it was tuned on and
// erupted through the skin as green wedges on a dozen others — the per-station
// profile of these models is simply not trustworthy enough to inset against.
//
// The body of the interior is the AEROPLANE'S OWN SKIN, seen from inside. It
// is the exact shape by construction, it costs no geometry, and there is
// nothing left to stick out.
function lineHullInside(model) {
  model.traverse((o) => {
    if (!o.isMesh) return;
    for (const m of (Array.isArray(o.material) ? o.material : [o.material])) {
      if (!m || (m.transparent && m.opacity < 0.95)) continue;
      m.side = THREE.DoubleSide;
      m.shadowSide = THREE.FrontSide; // or the hull shadows its own interior
      m.needsUpdate = true;
    }
  });
  return model;
}

function glazeHood(model) {
  const box = new THREE.Box3().setFromObject(model, true);
  const size = box.getSize(new THREE.Vector3());
  const b = new THREE.Box3();
  const c = new THREE.Vector3();
  model.traverse((o) => {
    if (!o.isMesh) return;
    const mats = (Array.isArray(o.material) ? o.material : [o.material]).filter(Boolean);
    const mn = mats.map((m) => m.name ?? '').join(' ');
    const hood = (HOOD_RX.test(o.name) || HOOD_RX.test(mn))
      && !(NOT_HOOD_RX.test(o.name) || NOT_HOOD_RX.test(mn));
    if (!hood) return;
    // the same sanity the cockpit measurement applies: a "canopy" as wide as
    // the wing is the fuselage skin under another name
    b.setFromObject(o, true);
    const s = b.getSize(new THREE.Vector3());
    b.getCenter(c);
    if (s.x > size.x * 0.4 || Math.abs(c.x) > size.x * 0.25) return;
    for (const m of mats) {
      if (m.transparent && m.opacity < 0.95) continue; // already glass
      // Remember that this one was PAINTED ON. A cockpit mesh the model ships
      // opaque is the well the pilot sits down in; one it ships transparent is
      // a canopy over him. They look identical once both are glass, and
      // telling them apart is what decides where the cockpit FLOOR is.
      o.userData.hoodWasOpaque = true;
      m.transparent = true;
      m.opacity = 0.38;
      m.depthWrite = false;   // or the pilot behind it is never drawn
      if (m.color) m.color.lerp(GLASS_TINT, 0.65);
      if ('roughness' in m) m.roughness = 0.06;
      if ('metalness' in m) m.metalness = 0.1;
      m.needsUpdate = true;
    }
  });
  return model;
}
const GLASS_TINT = new THREE.Color(0x9fd2ff); // the Spitfire canopy's own blue

// What a modeller calls the blades, in the half-dozen languages these models
// come in. Case-insensitive, because the one rigged plane in the set
// (carpet.glb) names its propeller bones "Propeller.L" with a capital P and
// the old startsWith('propeller') walked straight past them.
const PROP_NAME_RX = /^(propeller|prop[._\d]|pervane|helice|h[eé]lice|airscrew|vint|screw[._\d])/i;

async function loadGlbModel(file) {
  const gltf = await newGltfLoader().loadAsync(file);
  const model = gltf.scene;
  model.traverse((child) => {
    // GLB props already have the pivot on the spin axis (set in Blender).
    if (PROP_NAME_RX.test(child.name)) child.userData.isPropeller = true;
  });
  return model;
}

async function addInterior(model, def) {
  try {
    const g = await newGltfLoader().loadAsync(def.file.replace(/\.glb$/, '_cockpit.glb'));
    g.scene.traverse((o) => {
      if (!o.isMesh) return;
      // Tagged, because everything that MEASURES an aeroplane — the cockpit,
      // the hull profile, the box shell you stand on — walks the same tree,
      // and an interior skin 5 cm inside the hull would be read as the hull.
      o.userData.interior = true;
      o.castShadow = false;
      o.receiveShadow = true;
      o.frustumCulled = false;
    });
    g.scene.name = 'cockpit_interior';
    model.add(g.scene);
    return true;
  } catch {
    return false; // this one has no interior; the double-sided hull still shows
  }
}

export async function loadPlane(id) {
  const def = planeById(id);
  if (!cache.has(def.id)) {
    // Both paths get the same treatment: the hood glazed so you can see in,
    // and the hull lined from the inside so there is something to see. The
    // Spitfire comes through loadSpitfireModel and used to miss both.
    const raw = def.file.endsWith('.obj')
      ? await loadSpitfireModel() : await loadGlbModel(def.file);
    // THE INTERIOR THIS AEROPLANE WAS CUT FOR. tools/build_plane_interiors.py
    // takes the real airframe, carves out the slab of fuselage around its
    // canopy, pushes that skin in by its own thickness and turns it to face
    // inward — so the cockpit has the shape of THIS machine's body. It goes
    // in as a child of the raw model, which is what the orientation fix, the
    // centring, the scale and the belly drop all act on, so it lines up with
    // the hull by construction.
    if (!def.file.endsWith('.obj')) await addInterior(raw, def);
    const ready = lineHullInside(glazeHood(raw));
    // FIND THE BLADES, once per aircraft type, on the cached model every clone
    // comes from. Only the propeller-driven types: a jet has nothing to look
    // for and the search is not free.
    if (def.prop) {
      const t0 = performance.now();
      // Where this FILE's nose points. The orientation fix (def.yaw/def.roll)
      // is applied to a wrapper around the model, not to the model, so half
      // these files are stored tail-first or sideways and the detector would
      // be measuring "thin along the nose axis" across the wingspan.
      const nose = new THREE.Vector3(0, 0, -1).applyQuaternion(
        new THREE.Quaternion()
          .setFromEuler(new THREE.Euler(0, def.yaw ?? 0, def.roll ?? 0))
          .invert()
      );
      const found = tagPropellers(ready, nose, def.props ?? 1,
        def.propMake ? { make: def.propMake }
          : def.propHubs
            ? { hubs: def.propHubs, radius: def.propRadius, depth: def.propDepth }
            : null);
      console.log(`${def.id}: ${found.length} propeller(s)`
        + ` in ${(performance.now() - t0).toFixed(0)} ms`);
    }
    cache.set(def.id, ready);
  }
  // SkeletonUtils.clone, NOT Object3D.clone: a plain clone of a rigged model
  // keeps pointing at the ORIGINAL skeleton, so the copy renders frozen in the
  // cached model's pose — it never turns with the aircraft. Two of these
  // planes (neonfly, carpet) are rigged, and that is exactly what they did.
  const model = cloneSkeleton(cache.get(def.id));
  // Not every source model was authored nose toward -Z: some are stored
  // tail-first, some sideways, one banked on its wing. `yaw`/`roll` in the
  // def straighten them here, so the hangar preview, the flight model and
  // the damage code all see the same convention. Measured, not guessed —
  // see tools/probe_props.mjs and tools/shot_planezoom.mjs.
  if (!def.yaw && !def.roll) return model;
  const fixed = new THREE.Group();
  // Pivot on the AIRFRAME, not on the file's origin: a model whose origin sits
  // off to one side swings out when rotated, and every consumer that measures
  // the result (wingspan for the wingtip smoke, size for collisions) reads a
  // bbox far bigger than the aircraft.
  model.position.sub(new THREE.Box3().setFromObject(model, true).getCenter(new THREE.Vector3()));
  fixed.add(model);
  fixed.rotation.set(0, def.yaw ?? 0, def.roll ?? 0);
  return fixed;
}

// ---- the cockpit, measured --------------------------------------------------
//
// These models are real-world size on the OUTSIDE and stylised on the INSIDE.
// The Spitfire's span is right to within 3 cm of the real aircraft, but the
// gap between its seat and its canopy is 0.66 m -- two thirds of a seated
// pilot -- and the aerobatic's cockpit well is 0.50 m deep. Put a 1.8 m
// personage in one of those and it wears the aeroplane.
//
// So the cockpit is MEASURED off each airframe's own geometry and the whole
// aircraft is grown until a pilot fits in it. The user's call: "make plane for
// human size ... our caracters must sit in plane good".
export const PILOT_SIT = 0.92;    // seat pan to the crown, on our 1.8 m bodies
export const PILOT_SHOULDER = 0.58; // seat pan to the shoulders
export const PILOT_WIDE = 0.52;     // across the shoulders
// what tools/build_cockpit.py drew the tub at, for the fit-down above
const TUB_HALF_W = 0.30;
const TUB_SILL = 0.46;
const PILOT_GROW_MAX = 2.2;      // a bad measurement must not make a 200 m toy
const PILOT_ROOM_MIN = 0.3;      // below this the measurement is broken, not tight

// Names and materials that mean "this is the greenhouse" / "this is the well
// the pilot sits in". Both lists are needed: some models give the glass
// (spitfire `cam`, f104 `Glass`), some only the black hole under it
// (aerobatic `cocpit_black`), and several give both.
const CANOPY_RX = /glass|canopy|cocpit|cockpit|kabina|windscreen|window|vidrio|cristal|staklo|^cam|_cam|hood/i;
const WELL_RX = /koltuk|seat|chair|cocpit|cockpit|kabina|interior|cabin|pilot/i;

// A model's cockpit well is usually not modelled at all, so the floor has to
// be guessed from the hull. Calibrated against the planes that DO carry a seat
// or a well mesh (tools/_probe_cockpit.mjs prints both columns): the floor sits
// this far down the hull, measured from the canopy top.
const FLOOR_RATIO = 0.62;

function boxOf(o, box = new THREE.Box3()) {
  return box.setFromObject(o, true);
}

// The hull's top and bottom along its length, on the centreline only -- wings,
// tailplanes and the undercarriage are off to the side and must not count.
// Sampled from the vertices, because a mesh bounding box says nothing about
// where the fuselage is at a given station.
function hullProfile(root, band, zMin, zMax, bins = 48, opaqueOnly = false) {
  const top = new Float64Array(bins).fill(-Infinity);
  const bot = new Float64Array(bins).fill(Infinity);
  const wid = new Float64Array(bins); // half-width, for the climb-aboard spot
  const v = new THREE.Vector3();
  const span = Math.max(1e-6, zMax - zMin);
  root.traverse((o) => {
    if (!o.isMesh || !o.geometry?.attributes?.position || o.userData.interior) return;
    if (opaqueOnly) {
      const mats = (Array.isArray(o.material) ? o.material : [o.material]).filter(Boolean);
      if (mats.some((m) => m.transparent && m.opacity < 0.95)) return;
    }
    const pos = o.geometry.attributes.position;
    // big meshes are sampled: a profile does not need every vertex
    const stride = Math.max(1, Math.floor(pos.count / 20000));
    for (let i = 0; i < pos.count; i += stride) {
      v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld);
      if (Math.abs(v.x) > band || v.z < zMin || v.z > zMax) continue;
      const b = Math.min(bins - 1, Math.floor(((v.z - zMin) / span) * bins));
      if (v.y > top[b]) top[b] = v.y;
      if (v.y < bot[b]) bot[b] = v.y;
      const ax = Math.abs(v.x);
      if (ax > wid[b]) wid[b] = ax;
    }
  });
  return { top, bot, wid, zMin, zMax, bins };
}

// Everything the pilot needs to know about an airframe: where the seat is, how
// much head room there is over it, and how much the whole aircraft has to grow
// before a 1.8 m personage fits. Measured in `root`'s own space.
//
// `override` (def.cockpit) wins over anything measured, for the models the
// rules read wrong. All of it is in the MODEL's own frame, before the aircraft
// is grown or dropped onto its wheels:
//   z      the station, measured along the length (tools/_probe_cockpit.mjs)
//   closed treat the canopy as glass even though the model's is opaque
//   y      the seat pan outright
//   room   the head room, which is what the growth is worked out from
//   grow   the growth itself, last resort
export function findCockpit(root, override = null) {
  root.updateMatrixWorld(true);
  const air = boxOf(root);
  const size = air.getSize(new THREE.Vector3());
  const len = size.z;
  const vol = Math.max(1e-6, size.x * size.y * size.z);
  const band = Math.max(0.3, size.x * 0.06);
  // the pilot station is never in the propeller and never in the tail
  const zMin = air.min.z + len * 0.05;
  const zMax = air.min.z + len * 0.78;

  // THE GREENHOUSE. Candidates are named, or see-through, and they have to be
  // SMALL and on the centreline: half these files call a mesh "Canopy" when it
  // is the whole aeroplane (the A-7's `Canopy_open_close_1` is 13 m across),
  // and a transparent material is just as often the whole fuselage skin.
  let glass = null;
  let well = null;
  const b = new THREE.Box3();
  const c = new THREE.Vector3();
  root.traverse((o) => {
    if (!o.isMesh || o.userData.interior) return;
    const mats = (Array.isArray(o.material) ? o.material : [o.material]).filter(Boolean);
    const mn = mats.map((m) => m.name ?? '').join(' ');
    const see = mats.some((m) => m.transparent && m.opacity < 0.95);
    const named = CANOPY_RX.test(o.name) || CANOPY_RX.test(mn);
    const isWell = WELL_RX.test(o.name) || WELL_RX.test(mn);
    if (!named && !isWell && !see) return;
    boxOf(o, b);
    const s = b.getSize(new THREE.Vector3());
    b.getCenter(c);
    if (s.x > size.x * 0.4) return;                 // a wing-wide "canopy" is the skin
    if (s.x * s.y * s.z > vol * 0.06) return;       // ... and so is a plane-sized one
    if (Math.abs(c.x) > size.x * 0.25) return;      // the pilot sits near the middle
    if (c.z < zMin || c.z > zMax) return;           // not in the prop, not in the fin
    const v = s.x * s.y * s.z;
    // GLASS and WELL are not the same thing and must not be mixed up. Glass is
    // see-through: a closed canopy the pilot has to fit UNDER. A well is the
    // opaque hole sunk into the deck that the pilot sits DOWN IN, with head
    // and shoulders out in the air. Which of the two a model gives decides how
    // much room the pilot needs, so they are collected apart.
    if (see && (!glass || v > glass.v)) glass = { v, box: b.clone(), name: o.name, obj: o };
    if ((named || isWell) && !see && (!well || v > well.v)) well = { v, box: b.clone(), name: o.name };
  });

  const prof = hullProfile(root, band, zMin, zMax);
  const hullAt = (z) => {
    const i = Math.min(prof.bins - 1, Math.max(0,
      Math.floor(((z - prof.zMin) / (prof.zMax - prof.zMin)) * prof.bins)));
    // neighbours too: a single bin can fall in a gap between parts
    let top = -Infinity; let bot = Infinity; let wid = 0;
    for (let k = Math.max(0, i - 1); k <= Math.min(prof.bins - 1, i + 1); k++) {
      if (prof.top[k] > top) top = prof.top[k];
      if (prof.bot[k] < bot) bot = prof.bot[k];
      if (prof.wid[k] > wid) wid = prof.wid[k];
    }
    return { top, bot, wid };
  };

  // Station: the canopy's, or — with nothing named — the first place behind the
  // nose where the hull has risen to its full forward height. That is the
  // greenhouse on a fighter and the flight deck on an airliner.
  //
  // NOT "the highest point of the hull line": the FIN is on the centreline
  // too, and it is the highest thing on most of these aircraft. That rule put
  // the C-130's pilot in its tail, measured 5 cm of head room there and grew
  // the aeroplane to 105 m. The search is capped at FWD_END for the same
  // reason — no cockpit is in the back half.
  const FWD_END = 0.45;
  let station;
  if (glass) station = glass.box.getCenter(new THREE.Vector3()).z;
  else if (well) station = well.box.getCenter(new THREE.Vector3()).z;
  else {
    const last = Math.max(1, Math.floor(prof.bins * (FWD_END - 0.05) / 0.73));
    let best = -Infinity;
    for (let i = 0; i < last; i++) if (prof.top[i] > best) best = prof.top[i];
    let hit = 0;
    for (let i = 0; i < last; i++) { if (prof.top[i] >= best * 0.92) { hit = i; break; } }
    station = prof.zMin + ((hit + 0.5) / prof.bins) * (prof.zMax - prof.zMin);
  }
  // A `z` override has to come in HERE, not at the end: the ceiling, the floor
  // and the growth are all measured AT the station, so moving the station
  // afterwards would leave every one of them describing somewhere else.
  if (Number.isFinite(override?.z)) station = override.z;
  let hull = hullAt(station);
  if (!Number.isFinite(hull.top) || !Number.isFinite(hull.bot)) {
    // the station landed in a gap between parts — take the deepest bin the
    // forward fuselage has instead of giving up and leaving the pilot nowhere
    let best = null;
    for (let i = 0; i < prof.bins; i++) {
      if (!Number.isFinite(prof.top[i]) || !Number.isFinite(prof.bot[i])) continue;
      const d = prof.top[i] - prof.bot[i];
      if (!best || d > best.d) best = { d, i };
    }
    if (!best) return null;
    station = prof.zMin + ((best.i + 0.5) / prof.bins) * (prof.zMax - prof.zMin);
    hull = hullAt(station);
  }

  // CLOSED OR OPEN. A canopy with glass over the deck is a box the whole
  // pilot has to fit inside, head included — 0.92 m of it. A cockpit that is
  // only a well in the deck is an open one: the pilot sits down in it with
  // the head and shoulders out in the air, so it has to hold 0.58 m and no
  // more. Measuring an open cockpit as if it were a closed one is what asked
  // for a 14 m aerobatic plane: its canopy is painted on, the deck line IS
  // the top of the cockpit, and nothing was ever going to fit under it.
  const deck = hull.top;
  // `closed: true` in a def forces the closed-canopy rule on a model whose
  // glass is opaque — several of these jets paint the canopy on, and without
  // it their pilot is treated as sitting in the open and rides with its head
  // through the hood.
  const closed = override?.closed ?? (!!glass && glass.box.max.y > deck - 0.02);
  // forced closed with no glass in the file: the deck line IS the hood
  const top = closed && glass ? glass.box.max.y : deck;
  // The floor: a seat or a modelled well if the file has one, else the hull
  // depth cut to the calibrated ratio — a pilot does not sit on the skin, the
  // structure and the tank are under the seat.
  // ONE MESH IS OFTEN BOTH. The aerobatic plane's `cocpit_black` is the hood
  // AND the well under it in a single piece, so once it is glazed it stops
  // being the "well" and the floor fell back to a guess -- which asked for
  // more growth than the real 0.5 m well needs. A glass canopy that reaches
  // below the deck is giving the floor as well as the ceiling.
  // A glass mesh counts as the floor only when it is a WELL — sunk a good
  // way into the deck, like the aerobatic's one-piece hood-and-tub. At a
  // 5 cm threshold an ordinary bubble canopy qualified, because its sill sits
  // about at the deck, and the F-104 and P-100 were grown half as big again
  // to fit a pilot between their sills and their glass.
  // A glass mesh gives the floor only when it is a WELL: a cockpit the model
  // painted on as a solid, like the aerobatic's one-piece hood-and-tub. Real
  // glass is a lid, and reading its bottom as the floor asks for a pilot to
  // fit between a canopy's SILL and its roof — which grew the F-104 by half
  // again for no reason.
  const floor = well ? well.box.min.y
    : glass?.obj?.userData.hoodWasOpaque && glass.box.min.y < deck - 0.25
      ? glass.box.min.y
      : Math.max(hull.bot, top - (top - hull.bot) * FLOOR_RATIO);
  const room = top - floor;
  const need = closed ? PILOT_SIT : PILOT_SHOULDER;
  // A cockpit cannot be 5 cm deep. When the numbers come out like that the
  // measurement has found something that is not a cockpit, and growing the
  // aircraft by what it asks for would be far worse than leaving it alone:
  // the plane keeps its size and the table flags it for an override.
  const sane = room >= PILOT_ROOM_MIN;
  // HEIGHT WAS ONLY HALF OF IT. A cockpit tall enough for a pilot can still
  // be too narrow for its shoulders, and then the body is inside the sides of
  // the aeroplane however neatly its head clears the glass. The width counts
  // too: the usable span is the hull's, less the structure either side.
  // ...but only where the width was actually MEASURED. `hull.wid` is capped
  // by the sampling band, so on a narrow-span jet it reports the band rather
  // than the fuselage, and trusting it grew five aeroplanes that had room all
  // along. A clipped reading is no reading.
  const across = hull.wid * 2 * 0.82;
  const widthGrow = hull.wid < band * 0.95
    ? Math.min(1.25, Math.max(1, PILOT_WIDE / Math.max(0.2, across)))
    : 1;
  const grow = sane
    ? Math.min(PILOT_GROW_MAX, Math.max(1, need / room, widthGrow))
    : 1;

  // THE PILOT HANGS FROM THE CANOPY, not from the floor. Where a cockpit has
  // more room than a pilot needs, sitting it on the measured floor drops it
  // out of sight — the Su-33's hull is deep at the cockpit because the intake
  // ducts are under it, and its pilot ended up slung beneath the aeroplane.
  // What anyone actually looks at is the head against the glass, so that is
  // what is held fixed: the seat hangs exactly a pilot's height below the
  // canopy, and never deeper than the floor.
  const seatY = Math.max(floor, top - need);

  const out = {
    seat: new THREE.Vector3(0, seatY, station),
    top, floor, room, grow, sane, closed, deck,
    halfW: Math.max(0.25, hull.wid), // how far out the hull stands at the cockpit
    // how long the opening is, for the liner that caps it
    wellLen: Math.max(0.9, (well ?? glass)
      ? ((well ?? glass).box.max.z - (well ?? glass).box.min.z) : hull.wid * 3),
    hood: closed && glass ? glass.obj : null, // the lid, for hingeHood below
    // only a closed canopy is a lid: under an open one the head is SUPPOSED
    // to be out above the deck, and clamping it down would bury the pilot
    ceil: closed ? top : Infinity,
    from: (closed && glass ? `glass:${glass.name}` : well ? `well:${well.name}` : 'hull profile')
      + (sane ? '' : ' UNMEASURED'),
  };
  if (override) {
    if (Number.isFinite(override.y)) out.seat.y = override.y;
    if (Number.isFinite(override.room)) {
      out.room = override.room;
      out.grow = Math.min(PILOT_GROW_MAX, Math.max(1, PILOT_SIT / override.room));
    }
    if (Number.isFinite(override.grow)) out.grow = override.grow;
    out.from += ' +override';
  }
  return out;
}

// ---- what is inside the cockpit ---------------------------------------------
// "inside this plane ... havent nothing in salon". Every one of these models
// is a hollow shell: glaze the hood and you are looking into an empty box.
// tools/build_cockpit.py builds the furniture -- seat, harness, stick, panel
// with its dials, rudder pedals, throttle, floor -- authored at the size of
// the 1.8 m personages with its ORIGIN ON THE SEAT PAN, which is the one
// point every aircraft here has been measured for. So it goes in at scale 1,
// at `cockpit.seat`, on the wrapper (not the scaled model group) exactly like
// the pilot, and it fits every aeroplane for the same reason the pilot does.
let _kitPromise = null;
export function loadCockpitKit() {
  _kitPromise ??= newGltfLoader().loadAsync('/models/cockpit.glb')
    .then((g) => {
      g.scene.traverse((o) => {
        if (!o.isMesh) return;
        o.castShadow = false;       // it lives inside a hull; shadows cost and show nothing
        o.receiveShadow = true;
        o.frustumCulled = false;    // it is 2 m of geometry attached to a moving aeroplane
      });
      return g.scene;
    })
    .catch((e) => {
      console.warn('cockpit interior failed to load:', e?.message ?? e);
      return null;
    });
  return _kitPromise;
}

function fitCockpitKit(wrapper) {
  const c = wrapper.userData.cockpit;
  if (!c) return;
  loadCockpitKit().then((kit) => {
    if (!kit || wrapper.userData.cockpitKit) return;
    const o = kit.clone(true);
    o.position.copy(c.seat);
    o.rotation.set(0, 0, 0); // authored nose-forward, same frame as the wrapper
    // THE TUB NARROWS TO THE AEROPLANE, and never the other way. The furniture
    // is pilot-sized and goes in as authored, but the tub walls are the one
    // part that can meet the hull: on a cockpit narrower than the 0.30 m they
    // were drawn at they are pulled in. Shrinking is always safe — growing is
    // how a green box ends up sticking out of a fuselage.
    const tub = o.getObjectByName('cockpit_tub');
    if (tub && wrapper.getObjectByName('cockpit_interior')) {
      // the aeroplane's own inside is the wall now; the drawn box would only
      // sit inside it saying the same thing in straight lines
      tub.visible = false;
    } else if (tub) {
      tub.scale.x = Math.min(1, ((c.halfW ?? 1) * 0.88) / TUB_HALF_W);
      // and its walls stop under the sill of whatever cockpit it is in: they
      // are drawn 0.46 m over the seat, which is taller than a shallow well
      tub.scale.y = Math.min(1, Math.max(0.35, (c.deck - c.seat.y) / TUB_SILL));
    }
    wrapper.add(o);
    wrapper.userData.cockpitKit = o;
  });
}

// ---- the airframe you can stand on ------------------------------------------
//
// "i need planes must be real, like boxes, i need to clumb on planes like
// assasin creed". The real airframes cannot be colliders: they run to 120k
// triangles, three.js raycasts a Mesh by brute force, and foot.js casts about
// six probes a frame — that is the whole frame gone. So each aeroplane is
// reduced to a couple of dozen BOXES measured off its own vertices: a column
// down the fuselage and one slab per station out along the wings. A wing
// becomes a surface you can stand on at wing height, the fuselage a wall
// beside it with a top to pull yourself onto, and the whole shell is ~300
// triangles. Invisible, and registered as a collidable by game.js only while
// the aircraft is parked and the hero is on foot.
const SHELL_SLABS = 14;
const SHELL_MIN_T = 0.12; // m — a wing with no thickness is nothing to stand on

export function buildPlaneShell(wrapper) {
  wrapper.updateMatrixWorld(true);
  const air = new THREE.Box3().setFromObject(wrapper, true);
  const size = air.getSize(new THREE.Vector3());
  if (!(size.z > 0.5)) return null;
  const v = new THREE.Vector3();
  const pts = [];
  wrapper.traverse((o) => {
    if (!o.isMesh || !o.geometry?.attributes?.position || o.userData.interior) return;
    const pos = o.geometry.attributes.position;
    const stride = Math.max(1, Math.floor(pos.count / 12000));
    for (let i = 0; i < pos.count; i += stride) {
      pts.push(v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld).clone());
    }
  });
  if (pts.length < 32) return null;
  // The fuselage's half-width, read off the nose — the one stretch of an
  // aeroplane with no wing attached to it.
  const noseEnd = air.min.z + size.z * 0.22;
  let fw = 0;
  for (const p of pts) if (p.z < noseEnd) fw = Math.max(fw, Math.abs(p.x));
  fw = Math.max(fw || 0, size.x * 0.04);

  const boxes = [];
  const add = (x0, x1, y0, y1, z0, z1) => {
    const sx = Math.max(x1 - x0, SHELL_MIN_T);
    const sy = Math.max(y1 - y0, SHELL_MIN_T);
    const sz = Math.max(z1 - z0, SHELL_MIN_T);
    const g = new THREE.BoxGeometry(sx, sy, sz);
    g.translate((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2);
    boxes.push(g);
  };
  for (let i = 0; i < SHELL_SLABS; i++) {
    const z0 = air.min.z + (size.z * i) / SHELL_SLABS;
    const z1 = air.min.z + (size.z * (i + 1)) / SHELL_SLABS;
    let cy0 = Infinity; let cy1 = -Infinity; let cx = 0;
    let ox0 = Infinity; let ox1 = -Infinity; let oy0 = Infinity; let oy1 = -Infinity;
    for (const p of pts) {
      if (p.z < z0 || p.z >= z1) continue;
      if (Math.abs(p.x) <= fw) {
        cy0 = Math.min(cy0, p.y); cy1 = Math.max(cy1, p.y); cx = Math.max(cx, Math.abs(p.x));
      } else {
        ox0 = Math.min(ox0, p.x); ox1 = Math.max(ox1, p.x);
        oy0 = Math.min(oy0, p.y); oy1 = Math.max(oy1, p.y);
      }
    }
    if (cy0 < cy1) add(-cx, cx, cy0, cy1, z0, z1);
    // the wing/tailplane slab: only out where it actually is, at ITS height,
    // so the hero stands on the wing and not on thin air at canopy height
    if (ox0 < ox1) add(ox0, ox1, oy0, oy1, z0, z1);
  }
  if (!boxes.length) return null;
  const geo = mergeGeometries(boxes, false);
  for (const b of boxes) b.dispose();
  if (!geo) return null;
  const shell = new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ visible: false }));
  shell.name = 'plane_shell';
  shell.visible = false;
  shell.userData.isPlaneShell = true;
  wrapper.add(shell);
  return shell;
}

// ---- the hood that opens ----------------------------------------------------
// A canopy is a lid on a hinge at its back edge: the front lifts, the pilot
// climbs in, it comes down again. The models have it welded shut, so the mesh
// is re-parented under a pivot sitting on that back edge.
//
// The hinge AXIS has to be worked out, not assumed. These meshes hang below
// the per-model orientation fix (`yaw`/`roll` in the def), so the pivot's own
// local X is the file's idea of sideways, not the aeroplane's — a canopy on
// the Tornado would have swung open through its own wing. The plane's +X is
// carried down into the pivot's frame instead.
const HOOD_SWING = 0.95; // rad — about 55 degrees, a canopy standing open

function hingeHood(wrapper, hood) {
  if (!hood?.parent) return null;
  wrapper.updateMatrixWorld(true);
  const box = new THREE.Box3().setFromObject(hood, true);     // wrapper space
  const c = box.getCenter(new THREE.Vector3());
  // nose is -Z, so the hinge goes on the AFT, upper edge of the glass
  const hingeW = new THREE.Vector3(c.x, box.max.y, box.max.z);
  const parent = hood.parent;
  const pivot = new THREE.Group();
  pivot.name = 'hood_hinge';
  pivot.position.copy(parent.worldToLocal(hingeW.clone()));
  parent.add(pivot);
  // keep the lid exactly where it is: its new local matrix is the old one
  // taken back by the hinge offset
  const m = new THREE.Matrix4().makeTranslation(
    -pivot.position.x, -pivot.position.y, -pivot.position.z).multiply(hood.matrix);
  m.decompose(hood.position, hood.quaternion, hood.scale);
  pivot.add(hood);
  pivot.updateMatrixWorld(true);
  const axis = new THREE.Vector3(1, 0, 0)
    .applyQuaternion(pivot.getWorldQuaternion(new THREE.Quaternion()).invert())
    .normalize();
  return { pivot, axis, open: 0 };
}

// 0 shut, 1 standing open. Called every frame by game.js.
export function setHoodOpen(plane, t) {
  const h = plane?.userData?.hood;
  if (!h) return;
  h.open = t;
  h.pivot.quaternion.setFromAxisAngle(h.axis, HOOD_SWING * t);
}

// Wraps the raw model in a group scaled/centered for gameplay: nose -Z, the
// belly at -WHEEL_HEIGHT so wheels touch the street while rolling.
export function makeGameplayPlane(model, def = planeById('spitfire')) {
  const group = new THREE.Group();
  // precise=true walks the vertices: for the models that carry an orientation
  // fix, a plain AABB gets inflated by the rotation and the plane reports a
  // wingspan half again as wide as it is
  const box = new THREE.Box3().setFromObject(model, true);
  const center = box.getCenter(new THREE.Vector3());
  model.position.sub(center);
  group.add(model);
  group.scale.setScalar(def.scale);

  const wrapper = new THREE.Group();
  wrapper.add(group);
  wrapper.updateMatrixWorld(true);

  // HUMAN SIZE. Measure the cockpit and grow the whole aircraft until a 1.8 m
  // personage fits in it — see findCockpit above. `def.cockpit: null` opts a
  // plane out entirely; an object overrides what the measurement got wrong.
  const cockpit = def.cockpit === null ? null : findCockpit(wrapper, def.cockpit);
  if (cockpit && cockpit.grow > 1.001) {
    group.scale.multiplyScalar(cockpit.grow);
    cockpit.seat.multiplyScalar(cockpit.grow); // measured before the growth
    for (const k of ['top', 'floor', 'room', 'deck', 'ceil', 'halfW', 'wellLen']) {
      cockpit[k] *= cockpit.grow;
    }
    wrapper.updateMatrixWorld(true);
  }

  const wbox = new THREE.Box3().setFromObject(wrapper, true);
  // GLB planes vary from 8.5 m racers to a 52 m transport: rest the belly at
  // wheel height and tell the game how wide the plane is for wingtip smoke /
  // clipping. The Spitfire used to be exempt because its 0.34 scale happened
  // to land it there; now that every airframe is grown to fit its pilot, that
  // coincidence is gone and it is seated like the rest (it rises 10 cm — its
  // wheels were that far into the road).
  const drop = wbox.min.y + 1.7; // WHEEL_HEIGHT in game.js
  group.position.y -= drop;
  // the seat AND the canopy over it, both in the finished plane's own frame
  if (cockpit) {
    for (const k of ['top', 'floor', 'deck', 'ceil']) cockpit[k] -= drop;
    cockpit.seat.y -= drop;
  }
  wrapper.userData.cockpit = cockpit; // where the pilot sits, in wrapper space
  wrapper.userData.hood = cockpit?.hood ? hingeHood(wrapper, cockpit.hood) : null;
  wrapper.userData.shell = buildPlaneShell(wrapper); // the boxes you climb on
  fitCockpitKit(wrapper);                           // seat, stick, panel, pedals
  wrapper.userData.halfSpan = (wbox.max.x - wbox.min.x) / 2;
  wrapper.userData.size = Math.max(wbox.max.x - wbox.min.x, wbox.max.z - wbox.min.z);
  // THE AIRFRAME'S OWN HEIGHT, measured here and nowhere else. `wbox` is taken
  // before the box shell, the cockpit kit and the pilot are attached, so it is
  // the aeroplane and only the aeroplane. Measuring `game.plane` at runtime
  // instead reads all of those as well -- a Spitfire comes back 276 m across,
  // which is how the hangar fit test refused every aircraft in the game.
  wrapper.userData.height = wbox.max.y - wbox.min.y;

  // Measure each propeller's spin axis off the finished aeroplane and give it
  // a blur disc. Has to happen HERE, not at load: the axis is wanted in the
  // propeller's own local frame and the orientation fix, the centring and the
  // human-size growth all sit between the file and this wrapper.
  wrapper.userData.propellers = finishPropellers(wrapper, new THREE.Vector3(0, 0, -1));

  // Main wings, for damage: classify by which side of the fuselage the mesh
  // sits on (sag/sol naming is not trusted — the model may be mirrored), and
  // remember each wingtip in wrapper space for the smoke emitter.
  const wings = {};
  wrapper.traverse((c) => {
    if (!c.isMesh) return;
    if (!c.name.startsWith('sagkanatsekil') && !c.name.startsWith('solkanatsekil')) return;
    const bb = new THREE.Box3().setFromObject(c);
    const center = bb.getCenter(new THREE.Vector3());
    const side = center.x > 0 ? 'right' : 'left';
    const tip = new THREE.Vector3(side === 'right' ? bb.max.x : bb.min.x, center.y, center.z);
    // Own material instance — damage darkens it without touching other clones.
    c.material = Array.isArray(c.material)
      ? c.material.map((m) => m.clone())
      : c.material.clone();
    wings[side] = { mesh: c, tip };
  });
  wrapper.userData.wings = wings;
  return wrapper;
}
