<?php
/**
 * Build a pre-provisioned Baikal SQLite database at image-build time so the
 * container serves CalDAV/CardDAV immediately, with no first-run web wizard and
 * no runtime provisioning one-shot (which would trip `docker compose up --wait`).
 *
 * Seeds, for each test user (user1@example.com / user2@example.com, password
 * "pass"): the Basic-auth credential row, the DAVACL principal, one default
 * calendar (VEVENT+VTODO) and one default addressbook. The suite's DavTaskTests
 * creates its own "Tasks" collection via MKCALENDAR, so none is seeded here.
 *
 * The schema is Baikal's own shipped DDL (Core/Resources/Db/SQLite/db.sql), so
 * it always matches the sabre/dav version in the image.
 */

const REALM   = 'BaikalDAV';                 // must match baikal.yaml auth_realm
const DB_PATH = '/var/www/baikal/Specific/db/db.sqlite';
const DDL     = '/var/www/baikal/Core/Resources/Db/SQLite/db.sql';

$users = [
	'user1@example.com' => 'pass',
	'user2@example.com' => 'pass',
];

@unlink(DB_PATH);
$pdo = new PDO('sqlite:' . DB_PATH);
$pdo->setAttribute(PDO::ATTR_ERRMODE, PDO::ERRMODE_EXCEPTION);

// 1) Schema, straight from Baikal's shipped DDL.
$pdo->exec(file_get_contents(DDL));

$insUser        = $pdo->prepare('INSERT INTO users (username, digesta1) VALUES (?, ?)');
$insPrincipal   = $pdo->prepare('INSERT INTO principals (uri, email, displayname) VALUES (?, ?, ?)');
$insCalendar    = $pdo->prepare('INSERT INTO calendars (synctoken, components) VALUES (1, ?)');
$insCalInstance = $pdo->prepare(
	'INSERT INTO calendarinstances
		(calendarid, principaluri, access, displayname, uri, description, calendarorder, calendarcolor, transparent)
	 VALUES (?, ?, 1, ?, ?, ?, 0, ?, 0)');
$insAddressBook = $pdo->prepare(
	'INSERT INTO addressbooks (principaluri, displayname, uri, description, synctoken)
	 VALUES (?, ?, ?, ?, 1)');

foreach ($users as $username => $password) {
	$principalUri = 'principals/' . $username;

	$insUser->execute([$username, md5($username . ':' . REALM . ':' . $password)]);
	$insPrincipal->execute([$principalUri, $username, $username]);

	$insCalendar->execute(['VEVENT,VTODO']);
	$calendarId = (int) $pdo->lastInsertId();
	$insCalInstance->execute([
		$calendarId, $principalUri, 'Default calendar', 'default',
		'Default calendar', '#0082c9ff',
	]);

	$insAddressBook->execute([$principalUri, 'Default Address Book', 'default', 'Default Address Book']);

	fwrite(STDERR, "seeded {$username}\n");
}

echo "Baikal seed complete: " . count($users) . " users\n";
