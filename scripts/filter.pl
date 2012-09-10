#!/usr/bin/perl -n

BEGIN {
    $file_ending = qr/\@\@(\\main(\\[\w\.]+)*\\\d+)?\r$/;
    @patterns = (
        # we need to know all interesting elements, even if they exist only in checkedout directories, but we skip checkedout *versions* (incremental export)
	qr/CHECKEDOUT\r$/,
	# general directories
	qr/\\directory_not_wanted/,
	qr/lost\+found/,

	# files
	qr/\.ccexclude$file_ending/,
	qr/\.mkelem$file_ending/,
	qr/\.\w+\.user$file_ending/,
	qr/\.suo$file_ending/,
	qr/\.contrib$file_ending/,
	qr/\.keep$file_ending/);
}

$skip = 0;

foreach $pattern (@patterns) {
    if (/$pattern/) {
	$skip = 1;
	last;
    }
}

next if $skip;

s/.*\\MyVob\\//;
print
